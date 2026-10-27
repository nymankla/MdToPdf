using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using static MdToPdf.PdfWriter;

namespace MdToPdf;

/// <summary>An RGB colour with components in 0..1.</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public string Fill => $"{Renderer.N(R)} {Renderer.N(G)} {Renderer.N(B)} rg";
    public string Stroke => $"{Renderer.N(R)} {Renderer.N(G)} {Renderer.N(B)} RG";
}

/// <summary>A page size in PDF points (1/72 inch).</summary>
internal sealed record Paper(string Name, double Width, double Height)
{
    public static readonly Paper A4 = new("a4", 595.28, 841.89);
    public static readonly Paper Letter = new("letter", 612, 792);

    /// <summary>Looks a paper size up by name, ignoring case; null when unknown.</summary>
    public static Paper? Find(string name) =>
        new[] { A4, Letter }.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Lays out Markdown blocks onto pages (A4 by default) and serializes them to PDF.</summary>
internal sealed class Renderer
{
    private const double Margin = 56, Bottom = Margin;
    private const double BaseSize = 10.5, Leading = 1.45, BlockGap = 8, ItemGap = 3;
    private const double CodeSize = 9, CodePad = 8, CellPadX = 6, CellPadY = 4;
    private const double Epsilon = 0.01; // absorbs float error when a column is sized to its exact content

    private static readonly Rgb Ink = new(0.12, 0.14, 0.16);
    private static readonly Rgb Muted = new(0.35, 0.38, 0.42);
    private static readonly Rgb LinkColor = new(0.04, 0.41, 0.85);
    private static readonly Rgb Shade = new(0.95, 0.96, 0.97);
    private static readonly Rgb Border = new(0.82, 0.84, 0.87);
    private static readonly double[] HeadingScale = [2.0, 1.5, 1.25, 1.0, 0.9, 0.85];

    private sealed class Page
    {
        public StringBuilder Under { get; } = new(); // backgrounds, drawn first
        public StringBuilder Over { get; } = new();  // text and lines
        public List<LinkArea> Links { get; } = [];
    }

    private sealed record LinkArea(double X0, double Y0, double X1, double Y1, string Target);
    private sealed record Anchor(int Level, string Title, string Slug, int Page, double Y);

    private sealed record Token(string Text, FontId Font, double Size, Rgb Color, string? Link, Style Style)
    {
        public double Width { get; init; } = Fonts.Width(Text, Font, Size);
    }

    private sealed record Line(List<(Token Token, double X)> Items, double Width, double Height, double Size);

    private sealed class OutlineNode(Anchor? anchor)
    {
        public Anchor? Anchor { get; } = anchor;
        public List<OutlineNode> Children { get; } = [];
        public int Id { get; set; }
        public int Descendants => Children.Sum(c => 1 + c.Descendants);
    }

    private readonly PdfWriter pdf = new();
    private readonly List<Page> pages = [];
    private readonly List<Anchor> anchors = [];
    private readonly Dictionary<string, int> slugCounts = [];
    private readonly Dictionary<string, PdfImage?> images = [];
    private readonly string baseDir;
    private readonly TextWriter warnings;
    private readonly Paper paper;
    private Page page;
    private double y;
    private Rgb ink = Ink;

    /// <param name="baseDir">Directory that relative image paths resolve against.</param>
    /// <param name="warnings">Receives non-fatal problems (missing images, unresolved anchors).</param>
    /// <param name="paper">Page size; A4 when null.</param>
    public Renderer(string baseDir, TextWriter warnings, Paper? paper = null)
    {
        this.baseDir = baseDir;
        this.warnings = warnings;
        this.paper = paper ?? Paper.A4;
        NewPage();
    }

    private double PageWidth => paper.Width;
    private double PageHeight => paper.Height;
    private double Top => PageHeight - Margin;
    private double ContentWidth => PageWidth - (2 * Margin);

    internal static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    public void Render(List<Block> blocks) => RenderBlocks(blocks, Margin, ContentWidth, 0, BlockGap);

    // ---------------------------------------------------------------- pagination

    [MemberNotNull(nameof(page))]
    private void NewPage()
    {
        page = new Page();
        pages.Add(page);
        y = Top;
    }

    private void EnsureSpace(double height)
    {
        if (y - height < Bottom && y < Top)
        {
            NewPage();
        }
    }

    private void Gap(double gap)
    {
        if (y < Top)
        {
            y -= gap;
        }
    }

    /// <summary>Calls <paramref name="draw"/> for each page slice between a start position and the current one.</summary>
    private void ForEachSlice(int startPage, double startY, Action<Page, double, double> draw)
    {
        for (var p = startPage; p < pages.Count; p++)
        {
            draw(pages[p], p == startPage ? startY : Top, p == pages.Count - 1 ? y : Bottom);
        }
    }

    // ---------------------------------------------------------------- blocks

    private void RenderBlocks(List<Block> blocks, double x, double w, int depth, double gap)
    {
        for (var k = 0; k < blocks.Count; k++)
        {
            if (k > 0)
            {
                Gap(gap);
            }

            switch (blocks[k])
            {
                case Heading h: RenderHeading(h, x, w); break;
                case Paragraph p: PlaceLines(Wrap(p.Content, BaseSize, Style.None, ink, w, Leading), x); break;
                case CodeBlock c: RenderCode(c, x, w); break;
                case Quote q: RenderQuote(q, x, w, depth); break;
                case ListBlock l: RenderList(l, x, w, depth); break;
                case Rule: RenderRule(x, w); break;
                case Table t: RenderTable(t, x, w); break;
                case ImageBlock i: RenderImage(i, x, w); break;
                default: throw new InvalidOperationException($"Unknown block {blocks[k].GetType().Name}");
            }
        }
    }

    private void RenderHeading(Heading h, double x, double w)
    {
        var size = BaseSize * HeadingScale[h.Level - 1];
        Gap(size * 0.6);
        var lines = Wrap(h.Content, size, Style.Bold, h.Level == 6 ? Muted : ink, w, 1.25);
        EnsureSpace(lines.Sum(l => l.Height) + (BaseSize * Leading * 2)); // keep with next

        var title = Inlines.PlainText(h.Content);
        anchors.Add(new Anchor(h.Level, title, UniqueSlug(Slug(title)), pages.Count - 1, y));

        PlaceLines(lines, x);
        if (h.Level <= 2)
        {
            y -= 3;
            HLine(x, w, y, Border, 0.75);
            y -= 3;
        }
    }

    private void RenderCode(CodeBlock c, double x, double w)
    {
        var lh = CodeSize * Leading;
        var maxChars = Math.Max(1, (int)((w - (2 * CodePad)) / (CodeSize * 0.6)));
        EnsureSpace((2 * CodePad) + lh);
        int startPage = pages.Count - 1;
        var startY = y;

        y -= CodePad;
        foreach (var line in c.Lines.DefaultIfEmpty("").Select(Fonts.Prepare))
        {
            for (var p = 0; p == 0 || p < line.Length; p += maxChars)
            {
                EnsureSpace(lh);
                var chunk = line.Substring(p, Math.Min(maxChars, line.Length - p));
                if (chunk.Length > 0)
                {
                    Text(page.Over, chunk, FontId.Mono, CodeSize, ink, x + CodePad, Baseline(y, lh, CodeSize));
                }

                y -= lh;
            }
        }

        y -= CodePad;
        ForEachSlice(startPage, startY, (pg, top, bottom) => Rect(pg.Under, x, bottom, w, top - bottom, Shade));
    }

    private void RenderQuote(Quote q, double x, double w, int depth)
    {
        EnsureSpace(BaseSize * Leading);
        int startPage = pages.Count - 1;
        var startY = y;

        var saved = ink;
        ink = Muted;
        RenderBlocks(q.Children, x + 14, w - 14, depth, BlockGap);
        ink = saved;

        ForEachSlice(startPage, startY, (pg, top, bottom) => Rect(pg.Under, x, bottom, 3, top - bottom, Border));
    }

    private void RenderList(ListBlock l, double x, double w, int depth)
    {
        var lh = BaseSize * Leading;
        var indent = l.Ordered
            ? Math.Max(18, Fonts.Width(Invariant($"{l.Start + l.Items.Count - 1}."), FontId.Regular, BaseSize) + 8)
            : 16;

        for (var k = 0; k < l.Items.Count; k++)
        {
            if (k > 0)
            {
                Gap(ItemGap);
            }

            var marker = l.Ordered ? Invariant($"{l.Start + k}.") : depth % 2 == 0 ? "•" : "–";
            EnsureSpace(lh);
            var mw = Fonts.Width(marker, FontId.Regular, BaseSize);
            Text(page.Over, marker, FontId.Regular, BaseSize, ink, x + indent - mw - 5, Baseline(y, lh, BaseSize));

            if (l.Items[k].Count == 0)
            {
                y -= lh;
            }

            RenderBlocks(l.Items[k], x + indent, w - indent, depth + 1, ItemGap + 1);
        }
    }

    private void RenderRule(double x, double w)
    {
        EnsureSpace(12);
        y -= 6;
        HLine(x, w, y, Border, 1.5);
        y -= 6;
    }

    private void RenderTable(Table t, double x, double w)
    {
        var size = BaseSize * 0.95;
        var cols = t.Header.Count;
        var natural = new double[cols];
        var minimum = new double[cols];
        foreach (var (row, bold) in t.Rows.Select(r => (r, false)).Prepend((t.Header, true)))
        {
            for (var j = 0; j < cols; j++)
            {
                var style = bold ? Style.Bold : Style.None;
                natural[j] = Math.Max(natural[j], Wrap(row[j], size, style, ink, double.MaxValue, Leading).Max(l => l.Width) + (2 * CellPadX));
                minimum[j] = Math.Max(minimum[j], LongestWord(row[j], size, style) + (2 * CellPadX));
            }
        }

        double sumNat = natural.Sum(), sumMin = minimum.Sum();
        var widths = sumNat <= w ? natural
            : sumMin >= w ? minimum.Select(m => m * w / sumMin).ToArray()
            : natural.Zip(minimum, (n, m) => m + ((n - m) * (w - sumMin) / (sumNat - sumMin))).ToArray();

        List<Line>[] Cells(List<List<Inline>> row, bool header) =>
            row.Select((cell, j) => Wrap(cell, size, header ? Style.Bold : Style.None, ink, widths[j] - (2 * CellPadX), Leading)).ToArray();

        static double RowHeight(List<Line>[] cells) => cells.Max(c => c.Sum(l => l.Height)) + (2 * CellPadY);

        void DrawRow(List<Line>[] cells, double h, bool header)
        {
            if (header)
            {
                Rect(page.Under, x, y - h, widths.Sum(), h, Shade);
            }

            var cx = x;
            for (var j = 0; j < cols; j++)
            {
                page.Over.Append(CultureInfo.InvariantCulture, $"{Border.Stroke} 0.75 w {N(cx)} {N(y - h)} {N(widths[j])} {N(h)} re S\n");
                var align = j < t.Aligns.Count ? t.Aligns[j] : Align.Left;
                var ly = y - CellPadY;
                foreach (var line in cells[j])
                {
                    var free = widths[j] - (2 * CellPadX) - line.Width;
                    DrawLine(line, cx + CellPadX + (align switch { Align.Right => free, Align.Center => free / 2, _ => 0 }), ly);
                    ly -= line.Height;
                }

                cx += widths[j];
            }

            y -= h;
        }

        var head = Cells(t.Header, true);
        var headH = RowHeight(head);
        var rows = t.Rows.Select(r => Cells(r, false)).ToList();

        void BreakPage()
        {
            NewPage();
            DrawRow(head, headH, true); // repeat the header on every page
        }

        // Keep the header with the first row, or with the first line of a row too tall for any page.
        var firstH = rows.Count == 0 ? 0 : RowHeight(rows[0]);
        if (firstH > Top - Bottom - headH)
        {
            firstH = RowHeight([.. rows[0].Select(c => FittingLines(c, 0, force: true))]);
        }

        EnsureSpace(headH + firstH);
        DrawRow(head, headH, true);
        foreach (var cells in rows)
        {
            var rest = cells;
            if (y - RowHeight(rest) < Bottom && RowHeight(rest) <= Top - Bottom - headH)
            {
                BreakPage(); // the row fits on a fresh page: move it there whole
            }

            // A row taller than a page is split between lines, one page-sized slice at a time.
            var fresh = false;
            while (y - RowHeight(rest) < Bottom)
            {
                var room = y - Bottom - (2 * CellPadY);
                var slice = rest.Select(c => FittingLines(c, room, force: fresh)).ToArray();
                if (slice.Any(c => c.Count > 0))
                {
                    DrawRow(slice, RowHeight(slice), false);
                    rest = [.. rest.Select((c, j) => c.GetRange(slice[j].Count, c.Count - slice[j].Count))];
                }

                BreakPage();
                fresh = true;
            }

            if (rest == cells || rest.Any(c => c.Count > 0))
            {
                DrawRow(rest, RowHeight(rest), false);
            }
        }
    }

    /// <summary>The leading lines whose total height fits in <paramref name="room"/>; at least one when <paramref name="force"/> is set.</summary>
    private static List<Line> FittingLines(List<Line> lines, double room, bool force)
    {
        var n = 0;
        for (var used = 0.0; n < lines.Count && (used + lines[n].Height <= room + Epsilon || (force && n == 0)); n++)
        {
            used += lines[n].Height;
        }

        return lines.GetRange(0, n);
    }

    private void RenderImage(ImageBlock block, double x, double w)
    {
        var img = LoadImage(block.Source);
        if (img is null)
        {
            var text = block.Alt.Length > 0 ? block.Alt : block.Source;
            PlaceLines(Wrap([new Inline($"[image: {text}]", Style.Italic, null)], BaseSize, Style.None, Muted, w, Leading), x);
            return;
        }

        // 96 dpi, shrunk to fit the column and the page.
        double iw = img.Width * 0.75, ih = img.Height * 0.75;
        var scale = Math.Min(1, Math.Min(w / iw, (Top - Bottom) / ih));
        iw *= scale;
        ih *= scale;
        EnsureSpace(ih);
        page.Over.Append(CultureInfo.InvariantCulture, $"q {N(iw)} 0 0 {N(ih)} {N(x)} {N(y - ih)} cm /Im{img.Id} Do Q\n");
        y -= ih;
    }

    private PdfImage? LoadImage(string source)
    {
        if (images.TryGetValue(source, out var cached))
        {
            return cached;
        }

        PdfImage? img = null;
        if (!source.Contains("://", StringComparison.Ordinal) && !source.StartsWith("data:", StringComparison.Ordinal))
        {
            try
            {
                var path = Path.Combine(baseDir, Uri.UnescapeDataString(source));
                img = File.Exists(path) ? Images.Embed(pdf, File.ReadAllBytes(path)) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException or InvalidDataException)
            {
                warnings.WriteLine($"warning: could not embed image '{source}': {e.Message}");
            }
        }

        if (img is null)
        {
            warnings.WriteLine($"warning: image '{source}' not embedded (missing, remote or unsupported)");
        }

        images[source] = img;
        return img;
    }

    // ---------------------------------------------------------------- text layout

    private static FontId FontFor(Style s) =>
        s.HasFlag(Style.Code) ? FontId.Mono
        : s.HasFlag(Style.Bold | Style.Italic) ? FontId.BoldItalic
        : s.HasFlag(Style.Bold) ? FontId.Bold
        : s.HasFlag(Style.Italic) ? FontId.Italic
        : FontId.Regular;

    private static double SizeFor(Style s, double size) => s.HasFlag(Style.Code) ? size * 0.9 : size;

    private static double LongestWord(List<Inline> content, double size, Style baseStyle) =>
        content.SelectMany(i => Fonts.Prepare(i.Text).Split(' ').Select(word => Fonts.Width(word, FontFor(i.Style | baseStyle), SizeFor(i.Style | baseStyle, size))))
            .DefaultIfEmpty(0).Max();

    /// <summary>Greedy line breaking of styled inline runs into lines no wider than <paramref name="width"/>.</summary>
    private static List<Line> Wrap(List<Inline> content, double size, Style baseStyle, Rgb color, double width, double leading)
    {
        var lines = new List<Line>();
        var current = new List<Token>();
        var word = new List<Token>();
        var currentWidth = 0.0;
        Token? space = null;

        void EndLine()
        {
            lines.Add(MakeLine(current, size, leading));
            current = [];
            currentWidth = 0;
            space = null;
        }

        void Add(Token t)
        {
            current.Add(t);
            currentWidth += t.Width;
        }

        void PlaceWord()
        {
            if (word.Count == 0)
            {
                return;
            }

            var w = word.Sum(t => t.Width);
            if (current.Count > 0 && currentWidth + (space?.Width ?? 0) + w > width + Epsilon)
            {
                EndLine();
            }

            if (current.Count > 0 && space is not null)
            {
                Add(space);
            }

            space = null;
            if (w <= width + Epsilon)
            {
                word.ForEach(Add);
            }
            else
            {
                // A single word wider than the line: break it between characters.
                foreach (var t in word)
                {
                    foreach (var ch in t.Text)
                    {
                        var piece = new Token(ch.ToString(), t.Font, t.Size, t.Color, t.Link, t.Style);
                        if (current.Count > 0 && currentWidth + piece.Width > width + Epsilon)
                        {
                            EndLine();
                        }

                        Add(piece);
                    }
                }
            }

            word.Clear();
        }

        foreach (var inline in content)
        {
            if (inline.Text == "\n")
            {
                PlaceWord();
                EndLine();
                continue;
            }

            var style = inline.Style | baseStyle;
            var (font, sz, col) = (FontFor(style), SizeFor(style, size), inline.Link is null ? color : LinkColor);
            var text = Fonts.Prepare(inline.Text);
            for (var p = 0; p < text.Length;)
            {
                if (text[p] == ' ')
                {
                    PlaceWord();
                    while (p < text.Length && text[p] == ' ')
                    {
                        p++;
                    }

                    space = new Token(" ", font, sz, col, inline.Link, style);
                    continue;
                }

                var q = text.IndexOf(' ', p);
                q = q < 0 ? text.Length : q;
                word.Add(new Token(text[p..q], font, sz, col, inline.Link, style));
                p = q;
            }
        }

        PlaceWord();
        if (current.Count > 0 || lines.Count == 0)
        {
            EndLine();
        }

        return lines;
    }

    /// <summary>Positions tokens on a line, merging neighbours that share a style into one text run.</summary>
    private static Line MakeLine(List<Token> tokens, double size, double leading)
    {
        static bool SameRun(Token a, Token b) =>
            a.Font == b.Font && a.Size == b.Size && a.Color == b.Color && a.Link == b.Link && a.Style == b.Style;

        // Each run is joined once and its width summed from the already measured tokens, so long lines stay linear.
        var items = new List<(Token Token, double X)>();
        var x = 0.0;
        for (var i = 0; i < tokens.Count;)
        {
            var j = i + 1;
            var width = tokens[i].Width;
            for (; j < tokens.Count && SameRun(tokens[i], tokens[j]); j++)
            {
                width += tokens[j].Width;
            }

            var run = j == i + 1 ? tokens[i]
                : tokens[i] with { Text = string.Concat(tokens.GetRange(i, j - i).Select(t => t.Text)), Width = width };
            items.Add((run, x));
            x += width;
            i = j;
        }

        var max = tokens.Select(t => t.Size).Append(size).Max();
        return new Line(items, x, max * leading, max);
    }

    private static double Baseline(double top, double lineHeight, double size) => top - ((lineHeight - size) / 2) - (size * 0.8);

    private void PlaceLines(List<Line> lines, double x)
    {
        foreach (var line in lines)
        {
            EnsureSpace(line.Height);
            DrawLine(line, x, y);
            y -= line.Height;
        }
    }

    private void DrawLine(Line line, double x, double top)
    {
        var baseline = Baseline(top, line.Height, line.Size);
        foreach (var (t, dx) in line.Items)
        {
            var tx = x + dx;
            if (t.Style.HasFlag(Style.Code))
            {
                Rect(page.Under, tx - 1.5, baseline - (t.Size * 0.3), t.Width + 3, t.Size * 1.25, Shade);
            }

            Text(page.Over, t.Text, t.Font, t.Size, t.Color, tx, baseline);
            if (t.Style.HasFlag(Style.Strike))
            {
                HLine(tx, t.Width, baseline + (t.Size * 0.3), t.Color, 0.6);
            }

            if (t.Link is not null)
            {
                page.Links.Add(new LinkArea(tx, baseline - (t.Size * 0.25), tx + t.Width, baseline + (t.Size * 0.9), t.Link));
            }
        }
    }

    // ---------------------------------------------------------------- drawing primitives

    private static void Text(StringBuilder sb, string text, FontId font, double size, Rgb color, double x, double baseline) =>
        sb.Append(CultureInfo.InvariantCulture, $"BT /F{(int)font} {N(size)} Tf {color.Fill} {N(x)} {N(baseline)} Td {Literal(text)} Tj ET\n");

    private static void Rect(StringBuilder sb, double x, double y, double w, double h, Rgb color) =>
        sb.Append(CultureInfo.InvariantCulture, $"{color.Fill} {N(x)} {N(y)} {N(w)} {N(h)} re f\n");

    private void HLine(double x, double w, double at, Rgb color, double thickness) =>
        page.Over.Append(CultureInfo.InvariantCulture, $"{color.Stroke} {N(thickness)} w {N(x)} {N(at)} m {N(x + w)} {N(at)} l S\n");

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append('-');
            }
        }

        return sb.ToString();
    }

    /// <summary>De-duplicates a slug the way GitHub does: the second "intro" becomes "intro-1", the third "intro-2".</summary>
    private string UniqueSlug(string slug)
    {
        var unique = slug;
        while (slugCounts.ContainsKey(unique))
        {
            slugCounts[slug]++;
            unique = Invariant($"{slug}-{slugCounts[slug]}");
        }

        slugCounts[unique] = 0;
        return unique;
    }

    /// <summary>PDF URIs must be 7-bit ASCII: percent-encode everything else as UTF-8.</summary>
    private static string EscapeUri(string uri)
    {
        var sb = new StringBuilder(uri.Length);
        foreach (var b in Encoding.UTF8.GetBytes(uri))
        {
            if (b is > 0x20 and < 0x7F)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- output

    public void Save(Stream output, string title)
    {
        var fonts = Fonts.BaseNames.Select(n => pdf.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{n} /Encoding /WinAnsiEncoding >>")).ToList();
        var fontRefs = string.Join(' ', fonts.Select((id, k) => Invariant($"/F{k} {id} 0 R")));
        var imageRefs = string.Join(' ', images.Values.OfType<PdfImage>().Select(i => Invariant($"/Im{i.Id} {i.Id} 0 R")));
        var resources = pdf.Add($"<< /Font << {fontRefs} >> /XObject << {imageRefs} >> >>");

        var pagesId = pdf.Reserve();
        var pageIds = pages.Select(_ => pdf.Reserve()).ToList();
        var targets = anchors.ToDictionary(a => a.Slug, a => Dest(a, pageIds));
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        for (var k = 0; k < pages.Count; k++)
        {
            var pg = pages[k];
            var footer = Invariant($"{k + 1} / {pages.Count}");
            Text(pg.Over, footer, FontId.Regular, 8, Muted, (PageWidth - Fonts.Width(footer, FontId.Regular, 8)) / 2, Margin / 2);
            var contents = pdf.AddStream("", Encoding.Latin1.GetBytes(pg.Under.Append(pg.Over).ToString()), compress: true);

            var annots = new List<int>();
            foreach (var link in pg.Links)
            {
                var action = link.Target.StartsWith('#')
                    ? targets.TryGetValue(link.Target[1..], out var dest) ? $"/Dest {dest}" : null
                    : $"/A << /S /URI /URI {Literal(EscapeUri(link.Target))} >>";
                if (action is null)
                {
                    unresolved.Add(link.Target);
                }
                else
                {
                    annots.Add(pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [{N(link.X0)} {N(link.Y0)} {N(link.X1)} {N(link.Y1)}] /Border [0 0 0] {action} >>"));
                }
            }

            var annotRefs = annots.Count == 0 ? "" : $" /Annots [{string.Join(' ', annots.Select(a => Invariant($"{a} 0 R")))}]";
            pdf.Set(pageIds[k], Invariant($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources {resources} 0 R /Contents {contents} 0 R{annotRefs} >>"));
        }

        foreach (var target in unresolved)
        {
            warnings.WriteLine($"warning: link '{target}' matches no heading");
        }

        pdf.Set(pagesId, Invariant($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => Invariant($"{id} 0 R")))}] /Count {pages.Count} >>"));
        var outline = WriteOutline(pageIds);
        var catalog = pdf.Add(Invariant($"<< /Type /Catalog /Pages {pagesId} 0 R{(outline > 0 ? Invariant($" /Outlines {outline} 0 R /PageMode /UseOutlines") : "")} >>"));
        var info = pdf.Add($"<< /Title {TextString(title)} /Producer (MdToPdf) /CreationDate (D:{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}Z) >>");
        pdf.Save(output, catalog, info);
    }

    private string Dest(Anchor a, List<int> pageIds) => Invariant($"[{pageIds[a.Page]} 0 R /XYZ null {N(Math.Min(a.Y + 4, PageHeight))} null]");

    /// <summary>Writes the heading tree as PDF bookmarks. Returns the outline root id, or 0 when there are no headings.</summary>
    private int WriteOutline(List<int> pageIds)
    {
        if (anchors.Count == 0)
        {
            return 0;
        }

        var root = new OutlineNode(null);
        var stack = new Stack<(int Level, OutlineNode Node)>();
        stack.Push((0, root));
        foreach (var a in anchors)
        {
            while (stack.Peek().Level >= a.Level)
            {
                stack.Pop();
            }

            var node = new OutlineNode(a);
            stack.Peek().Node.Children.Add(node);
            stack.Push((a.Level, node));
        }

        void AssignIds(OutlineNode n)
        {
            n.Id = pdf.Reserve();
            n.Children.ForEach(AssignIds);
        }

        string Kids(OutlineNode n) => n.Children.Count == 0 ? ""
            : Invariant($" /First {n.Children[0].Id} 0 R /Last {n.Children[^1].Id} 0 R /Count {n.Descendants}");

        void Write(OutlineNode parent)
        {
            for (var k = 0; k < parent.Children.Count; k++)
            {
                var n = parent.Children[k];
                var sb = new StringBuilder();
                sb.Append(CultureInfo.InvariantCulture, $"<< /Title {TextString(n.Anchor!.Title)} /Parent {parent.Id} 0 R /Dest {Dest(n.Anchor, pageIds)}");
                if (k > 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" /Prev {parent.Children[k - 1].Id} 0 R");
                }

                if (k < parent.Children.Count - 1)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" /Next {parent.Children[k + 1].Id} 0 R");
                }

                pdf.Set(n.Id, sb.Append(Kids(n)).Append(" >>").ToString());
                Write(n);
            }
        }

        AssignIds(root);
        pdf.Set(root.Id, $"<< /Type /Outlines{Kids(root)} >>");
        Write(root);
        return root.Id;
    }
}
