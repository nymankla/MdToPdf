using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using static MdToPdf.PdfSyntax;

namespace MdToPdf;

/// <summary>An RGB colour with components in 0..1.</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public string Fill => $"{N(R)} {N(G)} {N(B)} rg";
    public string Stroke => $"{N(R)} {N(G)} {N(B)} RG";
}

internal sealed record LinkArea(double X0, double Y0, double X1, double Y1, string Target);

internal sealed class Page
{
    public StringBuilder Under { get; } = new(); // backgrounds, drawn first
    public StringBuilder Over { get; } = new();  // text and lines
    public List<LinkArea> Links { get; } = [];
}

/// <summary>The growing list of pages, the layout cursor on the last one, and the drawing primitives.</summary>
internal sealed class Canvas
{
    public static readonly Rgb Shade = new(0.95, 0.96, 0.97);
    public static readonly Rgb Border = new(0.82, 0.84, 0.87);

    public Canvas(Paper paper, double margin)
    {
        Paper = paper;
        Margin = margin;
        NewPage();
    }

    public Paper Paper { get; }
    public double Margin { get; }
    public List<Page> Pages { get; } = [];
    public Page Current { get; private set; }
    public double Y { get; set; }

    public double Top => Paper.Height - Margin;
    public double Bottom => Margin;
    public double BodyHeight => Top - Bottom;
    public double ContentWidth => Paper.Width - (2 * Margin);
    public int PageIndex => Pages.Count - 1;

    // ---------------------------------------------------------------- pagination

    [MemberNotNull(nameof(Current))]
    public void NewPage()
    {
        Current = new Page();
        Pages.Add(Current);
        Y = Top;
    }

    public void EnsureSpace(double height)
    {
        if (Y - height < Bottom && Y < Top)
        {
            NewPage();
        }
    }

    public void Gap(double gap)
    {
        if (Y < Top)
        {
            Y -= gap;
        }
    }

    /// <summary>Calls <paramref name="draw"/> for each page slice between a start position and the current one.</summary>
    public void ForEachSlice(int startPage, double startY, Action<Page, double, double> draw)
    {
        for (var p = startPage; p < Pages.Count; p++)
        {
            draw(Pages[p], p == startPage ? startY : Top, p == Pages.Count - 1 ? Y : Bottom);
        }
    }

    /// <summary>Moves the pages from <paramref name="first"/> onwards in front of the others.</summary>
    public void MoveToFront(int first)
    {
        var moved = Pages.GetRange(first, Pages.Count - first);
        Pages.RemoveRange(first, moved.Count);
        Pages.InsertRange(0, moved);
    }

    // ---------------------------------------------------------------- drawing

    public static double Baseline(double top, double lineHeight, double size) => top - ((lineHeight - size) / 2) - (size * 0.8);

    public static void Text(StringBuilder sb, string text, FontId font, double size, Rgb color, double x, double baseline) =>
        sb.Append(CultureInfo.InvariantCulture, $"BT /F{(int)font} {N(size)} Tf {color.Fill} {N(x)} {N(baseline)} Td {Literal(text)} Tj ET\n");

    public static void Rect(StringBuilder sb, double x, double y, double w, double h, Rgb color) =>
        sb.Append(CultureInfo.InvariantCulture, $"{color.Fill} {N(x)} {N(y)} {N(w)} {N(h)} re f\n");

    public void Text(string text, FontId font, double size, Rgb color, double x, double baseline) =>
        Text(Current.Over, text, font, size, color, x, baseline);

    public void StrokeRect(double x, double y, double w, double h, Rgb color, double thickness) =>
        Current.Over.Append(CultureInfo.InvariantCulture, $"{color.Stroke} {N(thickness)} w {N(x)} {N(y)} {N(w)} {N(h)} re S\n");

    public void HLine(double x, double w, double at, Rgb color, double thickness) =>
        Current.Over.Append(CultureInfo.InvariantCulture, $"{color.Stroke} {N(thickness)} w {N(x)} {N(at)} m {N(x + w)} {N(at)} l S\n");

    /// <summary>A tick drawn as two strokes inside the box whose lower left corner is at (x, y).</summary>
    public void Tick(double x, double y, double size, Rgb color, double thickness) =>
        Current.Over.Append(CultureInfo.InvariantCulture, $"{color.Stroke} {N(thickness)} w {N(x + (size * 0.2))} {N(y + (size * 0.5))} m {N(x + (size * 0.42))} {N(y + (size * 0.25))} l {N(x + (size * 0.82))} {N(y + (size * 0.78))} l S\n");

    public void Image(PdfImage image, double x, double y, double w, double h) =>
        Current.Over.Append(CultureInfo.InvariantCulture, $"q {image.Matrix(x, y, w, h)} cm /Im{image.Id} Do Q\n");

    public void DrawLine(Line line, double x, double top)
    {
        var baseline = top - line.BaselineOffset;
        foreach (var (t, dx) in line.Items)
        {
            var tx = x + dx;
            var at = t.Style.HasFlag(Style.Super) ? baseline + (t.Size * 0.5) : baseline;
            if (t.Image is not null)
            {
                Image(t.Image, tx, at, t.Width, t.ImageHeight);
            }
            else
            {
                if (t.Style.HasFlag(Style.Code))
                {
                    Rect(Current.Under, tx - 1.5, at - (t.Size * 0.3), t.Width + 3, t.Size * 1.25, Shade);
                }

                Text(t.Text, t.Font, t.Size, t.Color, tx, at);
                if (t.Style.HasFlag(Style.Strike))
                {
                    HLine(tx, t.Width, at + (t.Size * 0.3), t.Color, 0.6);
                }
            }

            if (t.Link is not null)
            {
                var height = t.Image is null ? t.Size * 0.9 : t.ImageHeight;
                Current.Links.Add(new LinkArea(tx, at - (t.Size * 0.25), tx + t.Width, at + height, t.Link));
            }
        }
    }
}
