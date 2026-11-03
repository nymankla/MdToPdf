namespace MdToPdf;

/// <summary>Column sizing and page breaking for GFM tables.</summary>
internal sealed class TableLayout(Canvas canvas, TextLayout text, double baseSize)
{
    private const double CellPadX = 6, CellPadY = 4, Leading = 1.45;

    private double Size => baseSize * 0.95;

    private static Style StyleOf(bool header) => header ? Style.Bold : Style.None;

    private double[] ColumnWidths(Table t, double w, Rgb ink)
    {
        var cols = t.Header.Count;
        var natural = new double[cols];
        var minimum = new double[cols];
        foreach (var (row, header) in t.Rows.Select(r => (r, false)).Prepend((t.Header, true)))
        {
            for (var j = 0; j < cols; j++)
            {
                natural[j] = Math.Max(natural[j], text.Wrap(row[j], Size, StyleOf(header), ink, double.MaxValue, Leading).Max(l => l.Width) + (2 * CellPadX));
                minimum[j] = Math.Max(minimum[j], text.LongestWord(row[j], Size, StyleOf(header)) + (2 * CellPadX));
            }
        }

        double sumNat = natural.Sum(), sumMin = minimum.Sum();
        return sumNat <= w ? natural
            : sumMin >= w ? [.. minimum.Select(m => m * w / sumMin)]
            : [.. natural.Zip(minimum, (n, m) => m + ((n - m) * (w - sumMin) / (sumNat - sumMin)))];
    }

    private List<Line>[] Cells(List<List<Inline>> row, bool header, double[] widths, Rgb ink) =>
        [.. row.Select((cell, j) => text.Wrap(cell, Size, StyleOf(header), ink, widths[j] - (2 * CellPadX), Leading))];

    private static double RowHeight(List<Line>[] cells) => cells.Max(c => c.Sum(l => l.Height)) + (2 * CellPadY);

    /// <summary>The leading lines whose total height fits in <paramref name="room"/>; at least one when <paramref name="force"/> is set.</summary>
    private static List<Line> FittingLines(List<Line> lines, double room, bool force)
    {
        var n = 0;
        for (var used = 0.0; n < lines.Count && (used + lines[n].Height <= room + 0.01 || (force && n == 0)); n++)
        {
            used += lines[n].Height;
        }

        return lines.GetRange(0, n);
    }

    /// <summary>The height that has to stay together at the start of a table: the header and the first row or its first line.</summary>
    public double StartHeight(Table t, double w, Rgb ink)
    {
        var widths = ColumnWidths(t, w, ink);
        var headH = RowHeight(Cells(t.Header, true, widths, ink));
        return headH > canvas.BodyHeight / 2 ? headH : headH + FirstRowHeight(t, widths, headH, ink);
    }

    private double FirstRowHeight(Table t, double[] widths, double headH, Rgb ink)
    {
        if (t.Rows.Count == 0)
        {
            return 0;
        }

        var first = Cells(t.Rows[0], false, widths, ink);
        var height = RowHeight(first);
        return height > canvas.BodyHeight - headH ? RowHeight([.. first.Select(c => FittingLines(c, 0, force: true))]) : height;
    }

    public void Render(Table t, double x, double w, Rgb ink)
    {
        var widths = ColumnWidths(t, w, ink);

        void DrawRow(List<Line>[] cells, double h, bool shaded)
        {
            if (shaded)
            {
                Canvas.Rect(canvas.Current.Under, x, canvas.Y - h, widths.Sum(), h, Canvas.Shade);
            }

            var cx = x;
            for (var j = 0; j < widths.Length; j++)
            {
                canvas.StrokeRect(cx, canvas.Y - h, widths[j], h, Canvas.Border, 0.75);
                var align = j < t.Aligns.Count ? t.Aligns[j] : Align.Left;
                var ly = canvas.Y - CellPadY;
                foreach (var line in cells[j])
                {
                    var free = widths[j] - (2 * CellPadX) - line.Width;
                    canvas.DrawLine(line, cx + CellPadX + (align switch { Align.Right => free, Align.Center => free / 2, _ => 0 }), ly);
                    ly -= line.Height;
                }

                cx += widths[j];
            }

            canvas.Y -= h;
        }

        var head = Cells(t.Header, true, widths, ink);
        var headH = RowHeight(head);
        var rows = t.Rows.Select(r => (Cells: Cells(r, false, widths, ink), Header: false)).ToList();

        // A header taller than half a page can't repeat on every page: pages would hold little else, and one taller
        // than a page would never leave room for a row. It becomes an ordinary row that may split like any other.
        var repeat = headH <= canvas.BodyHeight / 2;
        var reserved = repeat ? headH : 0;

        void BreakPage()
        {
            canvas.NewPage();
            if (repeat)
            {
                DrawRow(head, headH, true);
            }
        }

        if (repeat)
        {
            canvas.EnsureSpace(headH + FirstRowHeight(t, widths, headH, ink));
            DrawRow(head, headH, true);
        }
        else
        {
            rows.Insert(0, (head, true));
        }

        foreach (var (cells, header) in rows)
        {
            var rest = cells;
            if (canvas.Y - RowHeight(rest) < canvas.Bottom && RowHeight(rest) <= canvas.BodyHeight - reserved)
            {
                BreakPage(); // the row fits on a fresh page: move it there whole
            }

            // A row taller than a page is split between lines, one page-sized slice at a time.
            // Each fresh page takes at least one line, and leaves room for the padding of an emptied row.
            var fresh = false;
            while (canvas.Y - RowHeight(rest) < canvas.Bottom)
            {
                var room = canvas.Y - canvas.Bottom - (2 * CellPadY);
                var slice = rest.Select(c => FittingLines(c, room, force: fresh)).ToArray();
                if (slice.Any(c => c.Count > 0))
                {
                    DrawRow(slice, RowHeight(slice), header);
                    rest = [.. rest.Select((c, j) => c.GetRange(slice[j].Count, c.Count - slice[j].Count))];
                }

                BreakPage();
                fresh = true;
            }

            if (rest == cells || rest.Any(c => c.Count > 0))
            {
                DrawRow(rest, RowHeight(rest), header);
            }
        }
    }
}
