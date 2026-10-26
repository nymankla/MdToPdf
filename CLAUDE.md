# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A dependency-free Markdown → PDF converter in C# (`mdtopdf <input.md> [output.pdf]`). It uses no NuGet packages, no browser and no font embedding. It publishes as a Native AOT single-file exe of about 1.8 MB. It targets `net10.0-windows10.0.22000.0` (set in `Directory.Build.props`, shared with the tests), so Windows 11 is the minimum OS. WinRT APIs are available, but AOT trims them away unless they are used. Keeping it lean is the main design goal. Don't add packages, and treat the limitations listed in README.md (WinAnsi-only text, standard-14 fonts, no raw HTML, no remote images) as deliberate.

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
```

If `publish` can't find vswhere, run `$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"` first.

There's no solution file on purpose. With one, the repo root would hold both a `.csproj` and a `.sln`, and a plain `dotnet build` would fail because it couldn't choose. `global.json` opts `dotnet test` into Microsoft.Testing.Platform. The .NET 10+ SDK no longer runs xUnit v3 through VSTest.

`dotnet format` rewrites line endings to CRLF, as `.editorconfig` requires. Run `dotnet format whitespace` if a newly written file fails the check with `ENDOFLINE`.

## Testing

**All new code must have tests.** This covers every new feature, Markdown construct, rendering behavior or bug fix. Add tests in the same change, and run the full suite before you finish. Fix the tests along with any behavior change that breaks them.

The tests are xUnit v3 in `tests/MdToPdf.Tests`, one class per source file. The main project exposes its internals to them through `InternalsVisibleTo`. The main project's `<Compile Remove="tests/**" />` keeps the test sources out of the exe. Test against observable behavior: the AST for parser code, and the produced PDF for the renderer. Don't widen visibility just for tests. The helpers in `Support/` make this easy:

- **`PdfDoc`:** renders Markdown (`PdfDoc.Render(md, baseDir)`) or saves a `PdfWriter` (`PdfDoc.From`). It parses the result into objects, decoded streams, page content and text runs (`Runs`/`Body`: font, size, x, y, text), so tests can assert on fonts, positions, colors, annotations and the outline.
- **`ImageBuilder`:** builds PNGs (any color type or depth, plus a forward row filter) and JPEG headers in memory.
- **`TempDir`:** a throwaway directory for CLI and image-file tests.

Render the `samples/` files as a visual check too. `sample.md` is the feature tour. `stress-table-code.md` and `stress-quote-list.md` test page breaks inside tables, code blocks, quotes and lists. `samples/*.pdf` is the only PDF output that isn't gitignored. The samples are also copied into the test output, where `SamplesRender` and `SampleImagesEmbed` use them.

## Lint configuration

`Directory.Build.props` sets `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`. `GenerateDocumentationFile` is on only so that IDE0005 (unused usings) runs on build, and CS1591 is suppressed. `.editorconfig` raises several rules to warnings, which means they are errors here:

- File-scoped namespaces.
- Unused members, parameters and assignments.
- Fields that should be readonly.
- **CA1305/CA1310:** all number formatting and string comparison must be culture-invariant. This matters because the output is a file format. Use `Renderer.N(double)` or `FormattableString.Invariant(...)` for every number written into PDF syntax.
- **Naming:** `const` and `static readonly` fields are PascalCase. Other private fields are camelCase with no underscore prefix.

## Architecture

The pipeline is `Program.cs` → `Cli.Run` (arguments, I/O, exit codes; takes writers so tests can capture output) → `Markdown.Parse` → `Renderer.Render` → `Renderer.Save` → `PdfWriter`. `Cli` renders the whole PDF into memory before it opens the output file, and turns I/O failures into one-line errors. Don't rely on `e.Message`, because the AOT build reduces it to a resource key. `Renderer` takes a `TextWriter` for warnings (missing images, `#links` that match no heading). Never write to `Console` directly.

- **AST** (`Markdown.cs`, `Inlines.cs`): `Block` is a sealed-record hierarchy: `Heading`, `Paragraph`, `CodeBlock`, `Quote`, `ListBlock`, `Rule`, `Table` and `ImageBlock`. `Quote` and list items hold nested `List<Block>`. Inline content is flattened to `List<Inline>`, where each `Inline` has `Text`, a `[Flags] Style` and an optional `Link`. The text `"\n"` stands for a hard break. The block parser calls `Inlines` to parse each block's text.
- **Layout** (`Renderer.cs`): a single top-down pass with a cursor (`page`, `y`). `EnsureSpace` and `NewPage` handle pagination. Each `Page` keeps two content buffers: `Under` for backgrounds and `Over` for text and lines. This lets backgrounds be drawn after their content has been laid out. Blocks that can span pages (code, quotes, tables) remember where they started. They then call `ForEachSlice` to paint a background or border on every page they cover. Table headers are re-emitted on each new page. A row that won't fit on the current page moves to the next one whole. If it's taller than a page, it is split between lines (`FittingLines`). `Wrap` and `MakeLine` break lines using `Token` widths from `Fonts.Width`.
- **Deferred resolution in `Save`:** links and headings are recorded during layout as `LinkArea` and `Anchor` (page index, y). They become PDF objects only in `Save`. Page object ids are reserved first, `#slug` links are resolved against the heading anchors (slugs are made unique the way GitHub does it: `intro`, `intro-1`, …), and the outline (bookmarks) tree is built from heading levels. Page footers (`n / total`) are also added in `Save`, because only then is the page count known.
- **PDF serialization** (`PdfWriter.cs`): a minimal object store with `Add`, `Reserve`/`Set` for forward references, and `AddStream` (optionally zlib/Flate-compressed). It writes the xref table and trailer.
- **Fonts** (`Fonts.cs`): `FontId` values map to resource names `/F0`–`/F4` (Helvetica family + Courier). Text is encoded to WinAnsi, and everything outside that set becomes `?`. `Fonts.Prepare` replaces common symbols with ASCII stand-ins (`→` becomes `->`) and drops invisible characters. The renderer applies it to all text it measures or draws. The parser leaves text alone, so the AST, bookmarks and the title keep full Unicode, and entities get the same stand-ins as literal characters. Link URIs are percent-encoded to ASCII. Content streams are written as Latin-1 bytes.
- **Images** (`Images.cs`): JPEG data is embedded unchanged as DCT. PNG data without alpha is embedded unchanged as Flate with Predictor 15. PNG with alpha is decoded and split into an image plus an SMask. Image paths are resolved relative to the input file's directory.
