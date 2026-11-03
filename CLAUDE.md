# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A dependency-free Markdown → PDF converter in C# (`mdtopdf [options] <input.md> [output.pdf]`; see README.md for the options). It uses no NuGet packages, no browser and no font embedding. It publishes as a Native AOT single-file exe of about 1.9 MB. Windows 11 is the minimum OS. WinRT APIs are available, but AOT trims them away unless they are used. Keeping it lean is the main design goal. Don't add packages, and treat the limitations listed in README.md (WinAnsi-only text, standard-14 fonts, no raw HTML, no remote images) as deliberate.

## Commands

```powershell
dotnet build                                    # main project only; any warning fails the build
dotnet test --project tests\MdToPdf.Tests       # builds both projects and runs all tests
dotnet test --project tests\MdToPdf.Tests --filter-class "MdToPdf.Tests.InlinesTests"
dotnet test --project tests\MdToPdf.Tests --filter-method "MdToPdf.Tests.InlinesTests.Entities"
dotnet format --verify-no-changes                       # lint main project
dotnet format tests\MdToPdf.Tests --verify-no-changes   # lint tests
dotnet run -- samples\sample.md                 # writes samples\sample.pdf next to the input
dotnet publish -c Release -r win-x64 -o publish # native AOT exe; needs VS C++ build tools
.\scripts\smoke-test.ps1 -Exe publish\mdtopdf.exe  # checks the native exe; CI runs it too
```

Release with `.\scripts\release.ps1 patch` (or `major`, `minor`, `revision`, or `-Version 1.2.3.4`). It bumps the four-part `<Version>` in `Directory.Build.props`, commits, tags `v<version>` and pushes; the tag triggers `.github/workflows/release.yml`. That workflow reuses `ci.yml` through `workflow_call`, so a release passes the same checks as a pull request. Don't hand-edit the version or set one in the csproj: the smoke test and the release workflow both compare against that file.

If `publish` can't find vswhere, run `$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"` first.

There's no solution file on purpose. With one, the repo root would hold both a `.csproj` and a `.sln`, and a plain `dotnet build` would fail because it couldn't choose. `global.json` opts `dotnet test` into Microsoft.Testing.Platform. The .NET 10+ SDK no longer runs xUnit v3 through VSTest.

`dotnet format` rewrites line endings to CRLF, as `.editorconfig` requires. Run `dotnet format whitespace` if a newly written file fails the check with `ENDOFLINE`.

## Testing

**All new code must have tests.** This covers every new feature, Markdown construct, rendering behavior or bug fix. Add tests in the same change, and run the full suite before you finish. Fix the tests along with any behavior change that breaks them.

The tests are xUnit v3 in `tests/MdToPdf.Tests`, one class per source file. The main project exposes its internals to them through `InternalsVisibleTo`. The main project's `<Compile Remove="tests/**" />` keeps the test sources out of the exe. Test against observable behavior: the AST for parser code, and the produced PDF for the renderer. Don't widen visibility just for tests. `RobustnessTests` asserts that random and hostile input always finishes, and `TableLayoutTests` guards page breaks with a time limit: add to them when you touch a loop that paginates or scans. `InvariantGlobalization` is set in `Directory.Build.props` so the tests run in the same mode as the exe. Keep it shared: it makes `string.Normalize` do nothing and removes named cultures, and a test host without it hid exactly that bug. Helpers are in `tests/MdToPdf.Tests/Support/` (`PdfDoc`, `ImageBuilder`, `TempDir`).

Render the `samples/` files as a visual check too. `sample.md` is the feature tour. `stress-table-code.md` and `stress-quote-list.md` test page breaks inside tables, code blocks, quotes and lists. `samples/*.pdf` is the only PDF output that isn't gitignored. The samples are also copied into the test output, where `SamplesRender` and `SampleImagesEmbed` use them.

## Lint configuration

`GenerateDocumentationFile` is on only so that IDE0005 (unused usings) runs on build, and CS1591 is suppressed. `.editorconfig` raises several rules to warnings, which means they are errors here:

- **CA1305/CA1310:** all number formatting and string comparison must be culture-invariant. This matters because the output is a file format. Use `Renderer.N(double)` or `FormattableString.Invariant(...)` for every number written into PDF syntax.

## Architecture

The pipeline is `Program.cs` → `Cli.Run` (arguments, I/O, exit codes; takes writers and streams so tests can capture output) → `Markdown.ParseDocument` → `Renderer.Render` → `Renderer.Save` → `PdfAssembler` → `PdfWriter`. `Cli` renders the whole PDF into memory, writes it to a temporary file and renames that over the output, and turns I/O failures into one-line errors. It refuses an output path equal to the input. Don't rely on `e.Message`, because the AOT build reduces it to a resource key. `Renderer` takes a `TextWriter` for warnings (missing images, `#links` that match no heading). Never write to `Console` directly.

- **Parsing order** (`Markdown.cs`, `Inlines.cs`): front matter and link/footnote definitions are removed in a pre-pass, because inlines are parsed during block parsing and definitions may follow their uses. Emphasis uses the CommonMark delimiter algorithm, and bracket pairs are computed once per text, so scanning stays linear. Depth caps live in `Limits`. Don't reintroduce a scan to the end of the text per marker.
- **Layout** (`Renderer.cs`, `TextLayout.cs`, `TableLayout.cs`, `Canvas.cs`): layout is a single top-down pass with a cursor (`page`, `y`). `EnsureSpace` and `NewPage` handle pagination. Each `Page` keeps two content buffers: `Under` for backgrounds and `Over` for text and lines. This lets backgrounds be drawn after their content has been laid out. Blocks that can span pages (code, quotes, tables) remember where they started. They then call `ForEachSlice` to paint a background or border on every page they cover. Table headers are re-emitted on each new page. A row that won't fit on the current page moves to the next one whole. If it's taller than a page, it is split between lines (`FittingLines`). A header taller than half a page isn't repeated: it becomes an ordinary row that can split, because a repeated one could leave no room and paginate forever. `KeepHeight` keeps a list marker with the start of its content. `--toc` is laid out last on its own pages and then moved to the front, shifting the anchors, so the body needs no second pass; footnotes are endnotes for the same single-pass reason. `Wrap` and `MakeLine` break lines using `Token` widths from `Fonts.Width`.
- **Deferred resolution in `PdfAssembler`:** links and headings are recorded during layout as `LinkArea` and `Anchor` (page index, y). They become PDF objects only there. An `Anchor` with level 0 (a footnote) is a link target but no bookmark. A relative link to a `.md` file becomes a `/GoToR` to the matching PDF. Page object ids are reserved first, `#slug` links are resolved against the heading anchors (slugs are made unique the way GitHub does it: `intro`, `intro-1`, …), and the outline (bookmarks) tree is built from heading levels. Page footers (`n / total`) are also added in `Save`, because only then is the page count known.
- **Fonts** (`Fonts.cs`): `FontId` values map to resource names `/F0`–`/F5` (Helvetica family, Courier and Courier-Bold). Text is encoded to WinAnsi, and everything outside that set becomes `?`. `Fonts.Compose` joins base letters with combining accents using its own table. `Fonts.Prepare` replaces common symbols with ASCII stand-ins (`→` becomes `->`) and drops invisible characters. The renderer applies it to all text it measures or draws, through `TextLayout.Prepare`, which collects lost characters for one warning at the end. The parser leaves text alone, so the AST, bookmarks and the title keep full Unicode, and entities get the same stand-ins as literal characters. Link URIs are percent-encoded to ASCII. Content streams are written as Latin-1 bytes.