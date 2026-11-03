<#
.SYNOPSIS
    Bumps the version in Directory.Build.props, then commits, tags and pushes the release.
.DESCRIPTION
    Pushing the tag v<version> starts the release workflow, which builds the Windows and Linux
    artifacts and creates the GitHub release. The commit and the tag are pushed together, so
    either both reach GitHub or neither does.
.PARAMETER Part
    Which part of major.minor.patch.revision to raise. The parts after it reset to 0.
.PARAMETER Version
    The exact new version, in four parts, instead of -Part.
.PARAMETER SkipTests
    Don't run the test suite before committing.
.PARAMETER DryRun
    Run the checks and show what would happen, without changing anything.
.EXAMPLE
    ./scripts/release.ps1 patch
.EXAMPLE
    ./scripts/release.ps1 -Version 2.0.0.0 -DryRun
#>
[CmdletBinding(DefaultParameterSetName = 'Part')]
param(
    [Parameter(ParameterSetName = 'Part', Mandatory = $true, Position = 0)]
    [ValidateSet('major', 'minor', 'patch', 'revision')]
    [string]$Part,

    [Parameter(ParameterSetName = 'Version', Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipTests,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$branch = 'main'
$root = Split-Path -Parent $PSScriptRoot
$props = Join-Path $root 'Directory.Build.props'
$versionPattern = '<Version>(\d+\.\d+\.\d+\.\d+)</Version>'

function Invoke-Git {
    & git -C $root @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

$content = [IO.File]::ReadAllText($props)
$match = [regex]::Match($content, $versionPattern)
if (-not $match.Success) { throw "No four-part <Version> found in $props" }
$current = [version]$match.Groups[1].Value

if ($PSCmdlet.ParameterSetName -eq 'Part') {
    $next = switch ($Part) {
        'major' { [version]::new($current.Major + 1, 0, 0, 0) }
        'minor' { [version]::new($current.Major, $current.Minor + 1, 0, 0) }
        'patch' { [version]::new($current.Major, $current.Minor, $current.Build + 1, 0) }
        'revision' { [version]::new($current.Major, $current.Minor, $current.Build, $current.Revision + 1) }
    }
}
else {
    $next = [version]$Version
    if ($next -le $current) { throw "The new version $next must be higher than the current $current" }
}

$tag = "v$next"

# Everything that can go wrong without touching anything comes first.
$onBranch = Invoke-Git rev-parse --abbrev-ref HEAD
if ($onBranch -ne $branch) { throw "Releases are made from $branch, but this is $onBranch" }

$dirty = Invoke-Git status --porcelain
if ($dirty) { throw "The working tree has uncommitted changes:`n$($dirty -join "`n")" }

Invoke-Git fetch --quiet --tags origin $branch
if ((Invoke-Git rev-parse HEAD) -ne (Invoke-Git rev-parse "origin/$branch")) { throw "$branch is not the same as origin/${branch}: pull or push first" }

if (Invoke-Git tag --list $tag) { throw "The tag $tag already exists" }

Write-Output "Releasing $current -> $next (tag $tag)"
if ($DryRun) {
    Write-Output 'Dry run: nothing was changed.'
    exit 0
}

# Keeps the file's line endings and encoding, so the commit shows just the version.
$updated = $content.Remove($match.Groups[1].Index, $match.Groups[1].Length).Insert($match.Groups[1].Index, "$next")
[IO.File]::WriteAllText($props, $updated, (New-Object Text.UTF8Encoding($false)))

try {
    if (-not $SkipTests) {
        & dotnet test --project (Join-Path $root 'tests/MdToPdf.Tests')
        if ($LASTEXITCODE -ne 0) { throw 'The tests failed' }
    }

    Invoke-Git add -- Directory.Build.props
    Invoke-Git commit --quiet -m "Release $tag"
}
catch {
    # Nothing has left this machine: put the version back, so the release can simply be run again.
    Invoke-Git reset --quiet -- Directory.Build.props
    [IO.File]::WriteAllText($props, $content, (New-Object Text.UTF8Encoding($false)))
    throw
}

Invoke-Git tag -a $tag -m "Release $tag"

try {
    Invoke-Git push --quiet --atomic origin $branch $tag
}
catch {
    Write-Warning "The push failed, so nothing reached GitHub. To retry: git push --atomic origin $branch $tag"
    Write-Warning "To undo locally: git tag -d $tag; git reset --hard HEAD~1"
    throw
}

Write-Output "Pushed $tag. The release workflow now builds the artifacts and creates the release."
