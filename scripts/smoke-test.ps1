<#
.SYNOPSIS
    Runs the published native exe on the samples and on inputs that only behave differently under AOT.
.PARAMETER Exe
    Path to mdtopdf.exe.
.PARAMETER OutDir
    Where the PDFs go; created if needed.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = 'smoke-output'
)

# Not 'Stop': Windows PowerShell would turn the exe's expected warnings on stderr into errors. Exit codes are checked instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function Fail([string]$message) {
    Write-Error $message
    exit 1
}

# Each sample must convert. Warnings are fine: the tour shows a missing image on purpose.
foreach ($sample in Get-ChildItem (Join-Path $root 'samples\*.md')) {
    $output = Join-Path $OutDir ($sample.BaseName + '.pdf')
    & $Exe --toc $sample.FullName $output 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "$($sample.Name): exit code $LASTEXITCODE" }
    if ((Get-Item $output).Length -lt 500) { Fail "$($sample.Name): output is too small" }
}

$version = & $Exe --version
if ($version -notmatch '^mdtopdf \d+\.\d+\.\d+$') { Fail "unexpected version output: $version" }

# Decomposed accents compose only through our own table, because InvariantGlobalization disables Normalize.
$accents = Join-Path $OutDir 'accents.md'
[IO.File]::WriteAllText($accents, "A$([char]0x030A) e$([char]0x0301)")
$warnings = & $Exe $accents 2>&1 | Where-Object { $_ -match 'warning' }
if ($warnings) { Fail "decomposed accents were not composed: $warnings" }

# A table header taller than a page must finish; it once looped until memory ran out.
$tall = Join-Path $OutDir 'tall-header.md'
$header = (1..80 | ForEach-Object { "h$_" }) -join '<br>'
[IO.File]::WriteAllText($tall, "| $header | b |`n|---|---|`n| 1 | 2 |`n")
$process = Start-Process -FilePath $Exe -ArgumentList "`"$tall`"" -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(30000)) {
    Stop-Process -Id $process.Id -Force
    Fail 'tall table header did not finish'
}
if ($process.ExitCode -ne 0) { Fail "tall table header: exit code $($process.ExitCode)" }

# The source must survive being named as the output.
$source = Join-Path $OutDir 'source.txt'
[IO.File]::WriteAllText($source, 'keep me')
& $Exe $source $source 2>$null | Out-Null
if ($LASTEXITCODE -ne 1) { Fail 'naming the input as the output should fail' }
if ([IO.File]::ReadAllText($source) -ne 'keep me') { Fail 'the input was overwritten' }

# Errors must be readable: AOT reduces exception messages to resource keys.
$errorText = & $Exe (Join-Path $OutDir 'no-such-file.md') 2>&1 | Out-String
if ($errorText -notmatch 'file not found') { Fail "unreadable error: $errorText" }

Write-Output "Smoke test passed: $version"
exit 0
