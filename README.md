# MdToPdf

A small, dependency-free Markdown → PDF converter in C#. No NuGet packages and no
browser. The only dependency is the .NET base class library. It builds to a ~1.8 MB native exe.

```
mdtopdf <input.md> [output.pdf]
```

If you leave out the output path, the PDF is written next to the input file.

## Supported Markdown

| Block | Inline |
|---|---|
| ATX (`#`) and setext headings | `*italic*`, `**bold**`, `***both***`, `_`/`__` |
| Paragraphs, hard breaks (two spaces, `\`, `<br>`) | `~~strikethrough~~` |
| Bullet and ordered lists, nested | `` `code` `` |
| Block quotes, nested | `[links](url)`, `<autolinks>`, bare `https://` URLs |
| Fenced (```` ``` ````, `~~~`) and indented code | `#heading` links jump inside the PDF |
| GFM tables with alignment | Backslash escapes, HTML entities |
| Horizontal rules | |
| Images: local JPEG and PNG (incl. alpha) | |

The PDF output also includes:

- A4 pages with page numbers in the footer.
- Bookmarks built from the headings.
- Clickable links.
- Table headers that repeat on each new page.
- Code block and quote backgrounds that continue across page breaks.

## Limitations (by design, to stay lean)

- **Fonts:** only the 14 standard PDF fonts (Helvetica, Courier), so nothing is embedded.
  Text is limited to Windows-1252: Latin-1 (å ä ö é ü …) plus € – — “ ” ‘ ’ • … ™.
  A few common symbols get ASCII stand-ins (`→` becomes `->`, `✓` becomes `x`). Everything else becomes `?`.
- **Images:** no remote images, no interlaced PNG, no GIF/SVG/WebP.
  Inline images (inside a paragraph) are shown as their alt text.
- **Not supported:** raw HTML (except `<br>` and comments), reference-style links, footnotes,
  syntax highlighting, task-list checkboxes.

## Build

```powershell
dotnet build                     # analyzers run; warnings fail the build
dotnet format --verify-no-changes  # style / formatting lint
dotnet run -- samples\sample.md  # try it
```

Native single-file exe (needs the Visual Studio C++ build tools):

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"  # if vswhere isn't found
dotnet publish -c Release -r win-x64 -o publish
```

## Layout

| File | Purpose |
|---|---|
| `src/Markdown.cs` | Block parser that produces the AST |
| `src/Inlines.cs` | Inline parser (emphasis, code, links, entities) |
| `src/Renderer.cs` | Layout: line breaking, pagination, tables, lists, outline |
| `src/PdfWriter.cs` | PDF object, stream and xref serialization |
| `src/Fonts.cs` | Standard-font width tables and WinAnsi encoding |
| `src/Images.cs` | JPEG passthrough, PNG passthrough or alpha decoding |
| `samples/` | Feature tour and page-break stress tests |
