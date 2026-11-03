namespace MdToPdf;

internal sealed record Token(string Text, FontId Font, double Size, Rgb Color, string? Link, Style Style)
{
    public double Width { get; init; } = Fonts.Width(Text, Font, Size);
    public PdfImage? Image { get; init; }
    public double ImageHeight { get; init; }
}

/// <summary>A laid-out line. BaselineOffset is the distance from its top down to its baseline.</summary>
internal sealed record Line(List<(Token Token, double X)> Items, double Width, double Height, double Size, double BaselineOffset);

/// <summary>Greedy line breaking of styled inline runs.</summary>
/// <param name="images">Resolves an image source; null when it can't be embedded.</param>
/// <param name="maxImageHeight">The tallest an inline image may be, normally a page.</param>
internal sealed class TextLayout(Func<string, PdfImage?> images, double maxImageHeight)
{
    private const double Epsilon = 0.01; // absorbs float error when a column is sized to its exact content
    private const double MinInlineImageWidth = 40; // what a table column must leave for an inline image, which shrinks to fit

    /// <summary>Code points that could not be shown and were replaced, for a warning at the end.</summary>
    public SortedSet<int> Unsupported { get; } = [];

    public string Prepare(string text) => Fonts.Prepare(text, Unsupported);

    private static FontId FontFor(Style s) =>
        s.HasFlag(Style.Code) ? s.HasFlag(Style.Bold) ? FontId.MonoBold : FontId.Mono
        : s.HasFlag(Style.Bold | Style.Italic) ? FontId.BoldItalic
        : s.HasFlag(Style.Bold) ? FontId.Bold
        : s.HasFlag(Style.Italic) ? FontId.Italic
        : FontId.Regular;

    private static double SizeFor(Style s, double size) =>
        s.HasFlag(Style.Super) ? size * 0.7 : s.HasFlag(Style.Code) ? size * 0.9 : size;

    private PdfImage? ImageOf(Inline inline) => inline.Image is null ? null : images(inline.Image);

    public double LongestWord(List<Inline> content, double size, Style baseStyle) =>
        content.Where(i => i.Text != "\n").SelectMany(i => ImageOf(i) is { } image
                ? [Math.Min(image.Fit(double.MaxValue, maxImageHeight).Width, MinInlineImageWidth)]
                : Prepare(i.Text).Split(' ').Select(word => Fonts.Width(word, FontFor(i.Style | baseStyle), SizeFor(i.Style | baseStyle, size))))
            .DefaultIfEmpty(0).Max();

    /// <summary>Breaks <paramref name="content"/> into lines no wider than <paramref name="width"/>.</summary>
    public List<Line> Wrap(List<Inline> content, double size, Style baseStyle, Rgb color, double width, double leading)
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
                // A single word wider than the line: break it between characters. An image can't be split.
                foreach (var t in word)
                {
                    if (t.Image is not null)
                    {
                        if (current.Count > 0)
                        {
                            EndLine();
                        }

                        Add(t);
                        continue;
                    }

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
            var (font, sz, col) = (FontFor(style), SizeFor(style, size), inline.Link is null ? color : Colors.Link);
            if (ImageOf(inline) is { } image)
            {
                var (iw, ih) = image.Fit(width, maxImageHeight);
                word.Add(new Token("", font, sz, col, inline.Link, style) { Image = image, Width = iw, ImageHeight = ih });
                continue;
            }

            var text = Prepare(inline.Text);
            for (var p = 0; p < text.Length;)
            {
                if (text[p] == ' ')
                {
                    PlaceWord();
                    var start = p;
                    while (p < text.Length && text[p] == ' ')
                    {
                        p++;
                    }

                    // Code keeps its spacing; elsewhere a run of spaces is one space.
                    space = new Token(style.HasFlag(Style.Code) ? text[start..p] : " ", font, sz, col, inline.Link, style);
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
            a.Image is null && b.Image is null
            && a.Font == b.Font && a.Size == b.Size && a.Color == b.Color && a.Link == b.Link && a.Style == b.Style;

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
        var height = max * leading;
        var offset = (height - max) / 2 + (max * 0.8);

        // An inline image sits on the baseline, so it makes the line taller above the text.
        var imageHeight = tokens.Select(t => t.ImageHeight).DefaultIfEmpty(0).Max();
        if (imageHeight > offset)
        {
            height += imageHeight - offset;
            offset = imageHeight;
        }

        return new Line(items, x, height, max, offset);
    }
}

internal static class Colors
{
    public static readonly Rgb Ink = new(0.12, 0.14, 0.16);
    public static readonly Rgb Muted = new(0.35, 0.38, 0.42);
    public static readonly Rgb Link = new(0.04, 0.41, 0.85);
}
