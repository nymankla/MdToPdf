using System.Globalization;
using static MdToPdf.PdfSyntax;

namespace MdToPdf;

/// <summary>Lays out Markdown blocks onto pages (A4 by default) and serializes them to PDF.</summary>
internal sealed class Renderer
{
    private const double Leading = 1.45, BlockGap = 8, ItemGap = 3, CodePad = 8;
    private const double QuoteIndent = 14, BulletIndent = 16, MarkerGap = 5, MinColumn = 72;
    private const double TocIndent = 12, TocNumberWidth = 30;

    private static readonly double[] HeadingScale = [2.0, 1.5, 1.25, 1.0, 0.9, 0.85];

    /// <summary>Where a block is laid out: its column, its list depth and its text colour.</summary>
    private readonly record struct Scope(double X, double W, int Depth, Rgb Ink)
    {
        /// <summary>The same scope moved right, unless that would leave the column too narrow to read.</summary>
        public Scope Indented(double by) => W - by >= MinColumn ? this with { X = X + by, W = W - by } : this;
    }

    private readonly PdfWriter pdf = new();
    private readonly List<Anchor> anchors = [];
    private readonly Slugger slugger = new();
    private readonly Dictionary<string, PdfImage?> images = [];
    private readonly Dictionary<string, PdfImage?> imagesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly string baseDir;
    private readonly TextWriter warnings;
    private readonly RenderOptions options;
    private readonly Canvas canvas;
    private readonly TextLayout text;
    private readonly TableLayout tables;
    private bool saved;

    /// <param name="baseDir">Directory that relative image paths resolve against.</param>
    /// <param name="warnings">Receives non-fatal problems (missing images, unresolved anchors).</param>
    /// <param name="paper">Page size; A4 when null.</param>
    public Renderer(string baseDir, TextWriter warnings, Paper? paper = null)
        : this(baseDir, warnings, new RenderOptions(paper ?? Paper.A4))
    {
    }

    public Renderer(string baseDir, TextWriter warnings, RenderOptions options)
    {
        this.baseDir = baseDir;
        this.warnings = warnings;
        this.options = options;
        canvas = new Canvas(options.Paper, options.Margin);
        text = new TextLayout(LoadImage, canvas.BodyHeight);
        tables = new TableLayout(canvas, text, BaseSize);
    }

    private double BaseSize => options.FontSize;
    private double CodeSize => BaseSize * 6 / 7;
    private double LineHeight => BaseSize * Leading;

    public void Render(List<Block> blocks)
    {
        RenderBlocks(blocks, new Scope(canvas.Margin, canvas.ContentWidth, 0, Colors.Ink), BlockGap);
        if (options.Toc)
        {
            RenderToc();
        }
    }

    public void Save(Stream output, string title, string? author = null, DateTime? created = null)
    {
        if (saved)
        {
            throw new InvalidOperationException("A rendered document can only be saved once.");
        }

        saved = true;
        if (text.Unsupported.Count > 0)
        {
            var shown = string.Join(", ", text.Unsupported.Take(10).Select(cp => Invariant($"U+{cp:X4}")));
            var more = text.Unsupported.Count > 10 ? ", ..." : "";
            warnings.WriteLine(Invariant($"warning: {text.Unsupported.Count} character(s) are not in the standard fonts and became '?': {shown}{more}"));
        }

        var embedded = images.Values.OfType<PdfImage>().Distinct();
        new PdfAssembler(pdf, canvas, anchors, embedded, warnings).Save(output, title, author, created);
    }

    // ---------------------------------------------------------------- blocks

    private void RenderBlocks(List<Block> blocks, Scope scope, double gap)
    {
        for (var k = 0; k < blocks.Count; k++)
        {
            if (k > 0)
            {
                canvas.Gap(gap);
            }

            switch (blocks[k])
            {
                case Heading h: RenderHeading(h, scope); break;
                case Paragraph p: PlaceLines(text.Wrap(p.Content, BaseSize, Style.None, scope.Ink, scope.W, Leading), scope.X); break;
                case CodeBlock c: RenderCode(c, scope); break;
                case Quote q: RenderQuote(q, scope); break;
                case ListBlock l: RenderList(l, scope); break;
                case Rule: RenderRule(scope); break;
                case Table t: tables.Render(t, scope.X, scope.W, scope.Ink); break;
                case ImageBlock i: RenderImage(i, scope); break;
                case FootnoteList f: RenderFootnotes(f, scope); break;
                default: throw new InvalidOperationException($"Unknown block {blocks[k].GetType().Name}");
            }
        }
    }

    private void RenderHeading(Heading h, Scope scope)
    {
        var size = BaseSize * HeadingScale[h.Level - 1];
        canvas.Gap(size * 0.6);
        var lines = text.Wrap(h.Content, size, Style.Bold, h.Level == 6 ? Colors.Muted : scope.Ink, scope.W, 1.25);
        canvas.EnsureSpace(lines.Sum(l => l.Height) + (LineHeight * 2)); // keep with next

        var title = Inlines.PlainText(h.Content);
        anchors.Add(new Anchor(h.Level, title, slugger.Unique(title), canvas.PageIndex, canvas.Y));

        PlaceLines(lines, scope.X);
        if (h.Level <= 2)
        {
            canvas.Y -= 3;
            canvas.HLine(scope.X, scope.W, canvas.Y, Canvas.Border, 0.75);
            canvas.Y -= 3;
        }
    }

    private void RenderCode(CodeBlock c, Scope scope)
    {
        var lh = CodeSize * Leading;
        var maxChars = Math.Max(1, (int)((scope.W - (2 * CodePad)) / (CodeSize * 0.6)));
        canvas.EnsureSpace((2 * CodePad) + lh);
        var startPage = canvas.PageIndex;
        var startY = canvas.Y;

        canvas.Y -= CodePad;
        foreach (var line in c.Lines.DefaultIfEmpty("").Select(text.Prepare))
        {
            for (var p = 0; p == 0 || p < line.Length; p += maxChars)
            {
                canvas.EnsureSpace(lh);
                var chunk = line.Substring(p, Math.Min(maxChars, line.Length - p));
                if (chunk.Length > 0)
                {
                    canvas.Text(chunk, FontId.Mono, CodeSize, scope.Ink, scope.X + CodePad, Canvas.Baseline(canvas.Y, lh, CodeSize));
                }

                canvas.Y -= lh;
            }
        }

        canvas.Y -= CodePad;
        canvas.ForEachSlice(startPage, startY, (pg, top, bottom) => Canvas.Rect(pg.Under, scope.X, bottom, scope.W, top - bottom, Canvas.Shade));
    }

    private void RenderQuote(Quote q, Scope scope)
    {
        canvas.EnsureSpace(LineHeight);
        var startPage = canvas.PageIndex;
        var startY = canvas.Y;

        RenderBlocks(q.Children, scope.Indented(QuoteIndent) with { Ink = Colors.Muted }, BlockGap);

        canvas.ForEachSlice(startPage, startY, (pg, top, bottom) => Canvas.Rect(pg.Under, scope.X, bottom, 3, top - bottom, Canvas.Border));
    }

    private void RenderList(ListBlock l, Scope scope)
    {
        var indent = l.Ordered
            ? Math.Max(18, Fonts.Width(Invariant($"{l.Start + l.Items.Count - 1}."), FontId.Regular, BaseSize) + 8)
            : BulletIndent;
        var inner = scope.Indented(indent) with { Depth = scope.Depth + 1 };

        for (var k = 0; k < l.Items.Count; k++)
        {
            if (k > 0)
            {
                canvas.Gap(ItemGap);
            }

            var item = l.Items[k];

            // The marker stays with the start of its content, even when that is a big image or a table.
            canvas.EnsureSpace(item.Count > 0 ? Math.Max(LineHeight, KeepHeight(item[0], inner)) : LineHeight);
            if (l.Tasks?[k] is { } done)
            {
                DrawCheckbox(inner.X, done, scope.Ink);
            }
            else
            {
                DrawMarker(l.Ordered ? Invariant($"{l.Start + k}.") : scope.Depth % 2 == 0 ? "•" : "–", inner.X, scope.Ink);
            }

            if (item.Count == 0)
            {
                canvas.Y -= LineHeight;
            }

            RenderBlocks(item, inner, ItemGap + 1);
        }
    }

    /// <summary>How much room a block needs right away, so that what precedes it isn't stranded on the previous page.</summary>
    private double KeepHeight(Block block, Scope scope) => block switch
    {
        Paragraph p => text.Wrap(p.Content, BaseSize, Style.None, scope.Ink, scope.W, Leading)[0].Height,
        Heading h => text.Wrap(h.Content, BaseSize * HeadingScale[h.Level - 1], Style.Bold, scope.Ink, scope.W, 1.25).Sum(l => l.Height) + (LineHeight * 2),
        CodeBlock => (2 * CodePad) + (CodeSize * Leading),
        Quote { Children: [var first, ..] } => KeepHeight(first, scope.Indented(QuoteIndent)),
        ListBlock { Items: [[var first, ..], ..] } => KeepHeight(first, scope.Indented(BulletIndent)),
        Table t => tables.StartHeight(t, scope.W, scope.Ink),
        ImageBlock i when LoadImage(i.Source) is { } image => image.Fit(scope.W, canvas.BodyHeight).Height,
        _ => LineHeight,
    };

    private void DrawMarker(string marker, double contentX, Rgb ink)
    {
        var width = Fonts.Width(marker, FontId.Regular, BaseSize);
        canvas.Text(marker, FontId.Regular, BaseSize, ink, contentX - width - MarkerGap, Canvas.Baseline(canvas.Y, LineHeight, BaseSize));
    }

    private void DrawCheckbox(double contentX, bool done, Rgb ink)
    {
        var size = BaseSize * 0.8;
        var x = contentX - size - MarkerGap;
        var y = Canvas.Baseline(canvas.Y, LineHeight, BaseSize) - (size * 0.1);
        canvas.StrokeRect(x, y, size, size, ink, 0.75);
        if (done)
        {
            canvas.Tick(x, y, size, ink, 1.2);
        }
    }

    private void RenderRule(Scope scope)
    {
        canvas.EnsureSpace(12);
        canvas.Y -= 6;
        canvas.HLine(scope.X, scope.W, canvas.Y, Canvas.Border, 1.5);
        canvas.Y -= 6;
    }

    private void RenderImage(ImageBlock block, Scope scope)
    {
        if (LoadImage(block.Source) is not { } image)
        {
            var label = block.Alt.Length > 0 ? block.Alt : block.Source;
            PlaceLines(text.Wrap([new Inline($"[image: {label}]", Style.Italic, null)], BaseSize, Style.None, Colors.Muted, scope.W, Leading), scope.X);
            return;
        }

        var (w, h) = image.Fit(scope.W, canvas.BodyHeight);
        canvas.EnsureSpace(h);
        canvas.Image(image, scope.X, canvas.Y - h, w, h);
        canvas.Y -= h;
    }

    /// <summary>Notes go at the end of the document, as on GitHub: footers can't be sized in a single top-down pass.</summary>
    private void RenderFootnotes(FootnoteList f, Scope scope)
    {
        canvas.Gap(BlockGap);
        canvas.EnsureSpace(LineHeight + 12);
        canvas.HLine(scope.X, Math.Min(scope.W, 120), canvas.Y, Canvas.Border, 0.75);
        canvas.Y -= 8;

        var indent = Fonts.Width(Invariant($"{f.Notes.Count}."), FontId.Regular, BaseSize) + 8;
        var inner = scope.Indented(Math.Max(18, indent));
        for (var k = 0; k < f.Notes.Count; k++)
        {
            if (k > 0)
            {
                canvas.Gap(ItemGap);
            }

            canvas.EnsureSpace(LineHeight);
            anchors.Add(new Anchor(0, "", Inlines.FootnoteAnchor(k + 1), canvas.PageIndex, canvas.Y));
            DrawMarker(Invariant($"{k + 1}."), inner.X, scope.Ink);
            if (f.Notes[k].Count == 0)
            {
                canvas.Y -= LineHeight;
            }

            RenderBlocks(f.Notes[k], inner, ItemGap + 1);
        }
    }

    /// <summary>Lays the contents out on pages of its own, then moves them in front of the body. Their number is known
    /// only after that, so the page numbers are drawn last and the body needs no second pass.</summary>
    private void RenderToc()
    {
        var entries = anchors.Where(a => a.Level > 0).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        canvas.NewPage();
        var firstTocPage = canvas.PageIndex;
        PlaceLines(text.Wrap([new Inline("Contents", Style.Bold, null)], BaseSize * HeadingScale[0], Style.None, Colors.Ink, canvas.ContentWidth, 1.25), canvas.Margin);
        canvas.Y -= BlockGap;

        var numbers = new List<(Page Page, double Baseline, Anchor Anchor)>();
        var dot = Fonts.Width(". ", FontId.Regular, BaseSize);
        var right = canvas.Margin + canvas.ContentWidth;
        foreach (var entry in entries)
        {
            var x = canvas.Margin + ((entry.Level - 1) * TocIndent);
            var width = Math.Max(MinColumn, right - x - TocNumberWidth);
            var lines = text.Wrap([new Inline(entry.Title, Style.None, null)], BaseSize, Style.None, Colors.Ink, width, Leading);
            foreach (var line in lines)
            {
                canvas.EnsureSpace(line.Height);
                canvas.DrawLine(line, x, canvas.Y);
                canvas.Current.Links.Add(new LinkArea(x, canvas.Y - line.Height, right, canvas.Y, "#" + entry.Slug));
                if (line == lines[^1])
                {
                    var baseline = canvas.Y - line.BaselineOffset;
                    var dots = (int)((right - TocNumberWidth - x - line.Width) / dot) - 1;
                    if (dots > 0)
                    {
                        canvas.Text(string.Concat(Enumerable.Repeat(". ", dots)), FontId.Regular, BaseSize, Colors.Muted, right - TocNumberWidth - (dots * dot), baseline);
                    }

                    numbers.Add((canvas.Current, baseline, entry));
                }

                canvas.Y -= line.Height;
            }
        }

        var tocPages = canvas.Pages.Count - firstTocPage;
        foreach (var (pg, baseline, entry) in numbers)
        {
            var label = (entry.Page + tocPages + 1).ToString(CultureInfo.InvariantCulture);
            Canvas.Text(pg.Over, label, FontId.Regular, BaseSize, Colors.Ink, right - Fonts.Width(label, FontId.Regular, BaseSize), baseline);
        }

        canvas.MoveToFront(firstTocPage);
        for (var k = 0; k < anchors.Count; k++)
        {
            anchors[k] = anchors[k] with { Page = anchors[k].Page + tocPages };
        }
    }

    private void PlaceLines(List<Line> lines, double x)
    {
        foreach (var line in lines)
        {
            canvas.EnsureSpace(line.Height);
            canvas.DrawLine(line, x, canvas.Y);
            canvas.Y -= line.Height;
        }
    }

    // ---------------------------------------------------------------- images

    private PdfImage? LoadImage(string source)
    {
        if (images.TryGetValue(source, out var cached))
        {
            return cached;
        }

        PdfImage? image = null;
        var problem = "unsupported or corrupt";
        if (source.Contains("://", StringComparison.Ordinal) || source.StartsWith("data:", StringComparison.Ordinal))
        {
            problem = "remote images are not supported";
        }
        else
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(source)));
                if (!imagesByPath.TryGetValue(path, out image)) // the same file may be named in other ways
                {
                    if (File.Exists(path))
                    {
                        image = Images.Embed(pdf, File.ReadAllBytes(path));
                        imagesByPath[path] = image;
                    }
                    else
                    {
                        problem = "file not found";
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problem = "cannot read the file";
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidDataException or NotSupportedException)
            {
                // A corrupt file; the default problem describes it.
            }
        }

        if (image is null)
        {
            warnings.WriteLine($"warning: image '{source}' not embedded: {problem}");
        }

        images[source] = image;
        return image;
    }
}
