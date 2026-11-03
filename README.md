# MdToPdf

A small, dependency-free Markdown → PDF converter in C#. No NuGet packages and no
browser. The only dependency is the .NET base class library. It builds to a ~1.9 MB native exe that requires Windows 11.

```
mdtopdf [options] <input.md> [output.pdf]
mdtopdf [options] <input.md> <input.md> ...   (or a pattern like *.md)
```

If you leave out the output path, each PDF is written next to its input file. The second name counts as the output unless it has a Markdown extension. Use `-` as the input to read stdin and as the output to write stdout; stdin goes to stdout by default.

| Option | Effect |
|---|---|
| `--paper a3\|a4\|a5\|letter\|legal` | Page size (default a4) |
| `--landscape` | Turn the page sideways |
| `--margin <pt>` | Page margin in points (default 56) |
| `--font-size <pt>` | Body text size in points (default 10.5) |
| `--toc` | Start with a table of contents |
| `--version` | Show the version |

Set the `SOURCE_DATE_EPOCH` environment variable (seconds since 1970) to get byte-identical PDFs from the same input.

## Supported Markdown

| Block | Inline |
|---|---|
| ATX (`#`) and setext headings | `*italic*`, `**bold**`, `***both***`, `_`/`__` |
| Paragraphs, hard breaks (two spaces, `\`, `<br>`) | `~~strikethrough~~` |
| Bullet and ordered lists, nested | `` `code` `` |
| Block quotes, nested | `[links](url)`, `<autolinks>`, bare `https://` URLs |
| Fenced (```` ``` ````, `~~~`) and indented code | `#heading` links jump inside the PDF |
| GFM tables with alignment | Backslash escapes, HTML entities |
| Horizontal rules | Reference links: `[text][ref]`, `[text][]`, `[ref]` |
| Images: local JPEG and PNG (incl. transparency) | Images inside text, links and table cells |
| Task lists: `- [x] done` | Footnotes: `[^1]` and `[^1]: note`, listed at the end |
| YAML front matter: `title` and `author` | HTML comments are hidden |

The PDF output also includes:

- Page numbers in the footer.
- Bookmarks built from the headings.
- Clickable links. A relative link to a `.md` file opens the PDF made from it.
- Table headers that repeat on each new page.
- Code block and quote backgrounds that continue across page breaks.

## Limitations (by design, to stay lean)

- **Fonts:** only the 14 standard PDF fonts (Helvetica, Courier), so nothing is embedded.
  Text is limited to Windows-1252: Latin-1 (å ä ö é ü …) plus € – — “ ” ‘ ’ • … ™.
  A few common symbols get ASCII stand-ins (`→` becomes `->`, `✓` becomes `x`). Everything else becomes `?`, with a warning listing what was lost.
- **Images:** no remote images, no interlaced PNG, no GIF/SVG/WebP.
- **Input files:** UTF-8 or UTF-16 with a byte order mark. Anything else is read as Windows-1252, with a warning.
- **Not supported:** raw HTML (except `<br>` and comments), syntax highlighting. Link and footnote
  definitions only count at the top level, not inside quotes or lists.
- **Nesting:** quotes and lists deeper than 32 levels are kept as plain text.

## Build

```powershell
dotnet build                     # analyzers run; warnings fail the build
dotnet format --verify-no-changes  # style / formatting lint (also: dotnet format tests\MdToPdf.Tests --verify-no-changes)
dotnet test --project tests\MdToPdf.Tests  # xUnit v3 test suite
dotnet run -- samples\sample.md  # try it
```

Native single-file exe (needs the Visual Studio C++ build tools):

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"  # if vswhere isn't found
dotnet publish -c Release -r win-x64 -o publish
.\scripts\smoke-test.ps1 -Exe publish\mdtopdf.exe  # checks the native exe itself
```

## Releases

The version lives in `Directory.Build.props` and has four parts. It reaches the exe's `--version` output and its file properties.

```powershell
.\scripts\release.ps1 patch            # or major, minor, revision
.\scripts\release.ps1 -Version 2.0.0.0  # an exact version
.\scripts\release.ps1 patch -DryRun     # only run the checks
```

The script checks that `main` is clean and in sync with GitHub, runs the tests, then commits the bump, tags it `v<version>` and pushes both together. The tag starts the release workflow, which runs the full CI build and publishes a GitHub release with `mdtopdf-<version>-win-x64.zip` and `SHA256SUMS`. A tag that doesn't match `Directory.Build.props` fails before anything is built.

## Layout

| File | Purpose |
|---|---|
| `src/Markdown.cs` | Block parser that produces the AST |
| `src/Inlines.cs` | Inline parser (emphasis, code, links, entities) |
| `src/Renderer.cs` | Block layout, lists, images, footnotes, table of contents |
| `src/TextLayout.cs` | Line breaking of styled text |
| `src/TableLayout.cs` | Column sizing and page breaks in tables |
| `src/Canvas.cs` | Pages, the layout cursor and drawing primitives |
| `src/PdfAssembler.cs` | Finished pages to PDF objects: links, bookmarks, footers |
| `src/PdfWriter.cs` | PDF object, stream and xref serialization |
| `src/PdfSyntax.cs` | Number and string formatting for PDF syntax |
| `src/Fonts.cs` | Standard-font width tables and WinAnsi encoding |
| `src/Images.cs` | JPEG passthrough, PNG passthrough or transparency decoding |
| `src/Slugger.cs` | Heading text to link targets |
| `src/Paper.cs` | Page sizes and layout options |
| `src/Limits.cs` | Caps on hostile or broken input |
| `src/Cli.cs` | Argument handling, file I/O and exit codes |
| `samples/` | Feature tours and page-break stress tests |
| `scripts/smoke-test.ps1` | Checks of the published native exe, also run in CI |
| `scripts/release.ps1` | Bumps the version, then commits, tags and pushes a release |
| `tests/MdToPdf.Tests/` | xUnit tests, one class per source file |
