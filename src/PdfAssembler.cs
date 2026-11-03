using System.Globalization;
using System.Text;
using static MdToPdf.PdfSyntax;

namespace MdToPdf;

/// <summary>A link target recorded during layout. Level 0 is not a heading: it stays out of the bookmarks and contents.</summary>
internal sealed record Anchor(int Level, string Title, string Slug, int Page, double Y);

/// <summary>Turns finished pages into PDF objects. Links, bookmarks and footers wait until here, when every page exists.</summary>
internal sealed class PdfAssembler(PdfWriter pdf, Canvas canvas, List<Anchor> anchors, IEnumerable<PdfImage> images, TextWriter warnings)
{
    private sealed class OutlineNode(Anchor? anchor)
    {
        public Anchor? Anchor { get; } = anchor;
        public List<OutlineNode> Children { get; } = [];
        public int Id { get; set; }
        public int Descendants => Children.Sum(c => 1 + c.Descendants);
    }

    public void Save(Stream output, string title, string? author, DateTime? created)
    {
        var fonts = Fonts.BaseNames.Select(n => pdf.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{n} /Encoding /WinAnsiEncoding >>")).ToList();
        var fontRefs = string.Join(' ', fonts.Select((id, k) => Invariant($"/F{k} {Ref(id)}")));
        var imageRefs = string.Join(' ', images.Select(i => Invariant($"/Im{i.Id} {Ref(i.Id)}")));
        var resources = pdf.Add($"<< /Font << {fontRefs} >> /XObject << {imageRefs} >> >>");

        var pagesId = pdf.Reserve();
        var pageIds = canvas.Pages.Select(_ => pdf.Reserve()).ToList();
        var targets = anchors.ToDictionary(a => a.Slug, a => Dest(a, pageIds));
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var paper = canvas.Paper;

        for (var k = 0; k < canvas.Pages.Count; k++)
        {
            var pg = canvas.Pages[k];
            var footer = Invariant($"{k + 1} / {canvas.Pages.Count}");
            Canvas.Text(pg.Over, footer, FontId.Regular, 8, Colors.Muted, (paper.Width - Fonts.Width(footer, FontId.Regular, 8)) / 2, canvas.Margin / 2);
            var contents = pdf.AddStream("", Encoding.Latin1.GetBytes(pg.Under.Append(pg.Over).ToString()), compress: true);

            var annots = new List<int>();
            foreach (var link in pg.Links)
            {
                if (Action(link.Target, targets) is { } action)
                {
                    annots.Add(pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [{N(link.X0)} {N(link.Y0)} {N(link.X1)} {N(link.Y1)}] /Border [0 0 0] {action} >>"));
                }
                else
                {
                    unresolved.Add(link.Target);
                }
            }

            var annotRefs = annots.Count == 0 ? "" : $" /Annots [{Refs(annots)}]";
            pdf.Set(pageIds[k], Invariant($"<< /Type /Page /Parent {Ref(pagesId)} /MediaBox [0 0 {N(paper.Width)} {N(paper.Height)}] /Resources {Ref(resources)} /Contents {Ref(contents)}{annotRefs} >>"));
        }

        foreach (var target in unresolved)
        {
            warnings.WriteLine($"warning: link '{target}' matches no heading");
        }

        pdf.Set(pagesId, Invariant($"<< /Type /Pages /Kids [{Refs(pageIds)}] /Count {canvas.Pages.Count} >>"));
        var outline = WriteOutline(pageIds);
        var catalog = pdf.Add(Invariant($"<< /Type /Catalog /Pages {Ref(pagesId)}{(outline > 0 ? Invariant($" /Outlines {Ref(outline)} /PageMode /UseOutlines") : "")} >>"));
        var authorEntry = author is null ? "" : $" /Author {TextString(author)}";
        var date = (created ?? DateTime.UtcNow).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var info = pdf.Add($"<< /Title {TextString(title)}{authorEntry} /Producer (MdToPdf) /CreationDate (D:{date}Z) >>");
        pdf.Save(output, catalog, info);
    }

    /// <summary>The annotation entry for a link target; null for a #link that matches nothing.</summary>
    private static string? Action(string target, Dictionary<string, string> targets)
    {
        if (target.StartsWith('#'))
        {
            return targets.TryGetValue(target[1..], out var dest) ? $"/Dest {dest}" : null;
        }

        if (ConvertedPdfPath(target) is { } path)
        {
            return $"/A << /S /GoToR /F << /Type /Filespec /F {Literal(path)} /UF {TextString(path)} >> /D [0 /Fit] >>";
        }

        return $"/A << /S /URI /URI {Literal(EscapeUri(target))} >>";
    }

    /// <summary>A relative link to a Markdown file points at the PDF made from it; null for anything else.
    /// A #fragment is dropped: the other PDF's headings aren't named destinations.</summary>
    internal static string? ConvertedPdfPath(string target)
    {
        var end = target.IndexOfAny(['#', '?']);
        var path = end < 0 ? target : target[..end];
        if (path.Contains(':', StringComparison.Ordinal) || path.StartsWith('/') || path.StartsWith('\\'))
        {
            return null; // a scheme, a drive or a server root: not relative
        }

        var markdown = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
        return markdown ? Path.ChangeExtension(Uri.UnescapeDataString(path), ".pdf") : null;
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

    private string Dest(Anchor a, List<int> pageIds) => Invariant($"[{Ref(pageIds[a.Page])} /XYZ null {N(Math.Min(a.Y + 4, canvas.Paper.Height))} null]");

    /// <summary>Writes the heading tree as PDF bookmarks. Returns the outline root id, or 0 when there are no headings.</summary>
    private int WriteOutline(List<int> pageIds)
    {
        var headings = anchors.Where(a => a.Level > 0).ToList();
        if (headings.Count == 0)
        {
            return 0;
        }

        var root = new OutlineNode(null);
        var stack = new Stack<(int Level, OutlineNode Node)>();
        stack.Push((0, root));
        foreach (var a in headings)
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

        static string Kids(OutlineNode n) => n.Children.Count == 0 ? ""
            : Invariant($" /First {Ref(n.Children[0].Id)} /Last {Ref(n.Children[^1].Id)} /Count {n.Descendants}");

        void Write(OutlineNode parent)
        {
            for (var k = 0; k < parent.Children.Count; k++)
            {
                var n = parent.Children[k];
                var sb = new StringBuilder();
                sb.Append(CultureInfo.InvariantCulture, $"<< /Title {TextString(n.Anchor!.Title)} /Parent {Ref(parent.Id)} /Dest {Dest(n.Anchor, pageIds)}");
                if (k > 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" /Prev {Ref(parent.Children[k - 1].Id)}");
                }

                if (k < parent.Children.Count - 1)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" /Next {Ref(parent.Children[k + 1].Id)}");
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
