using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MdToPdf.Tests.Support;

/// <summary>A text run drawn by a <c>BT ... Tj ET</c> operator.</summary>
internal sealed record TextRun(int Font, double Size, double X, double Y, string Text);

/// <summary>Just enough of a PDF reader to inspect what <see cref="PdfWriter"/> produces.</summary>
internal sealed partial class PdfDoc
{
    private static readonly Encoding WinAnsi = CreateWinAnsi();

    private PdfDoc(byte[] bytes)
    {
        Bytes = bytes;
        Raw = Encoding.Latin1.GetString(bytes);
    }

    public byte[] Bytes { get; }

    /// <summary>The whole file as Latin-1, so string indices equal byte offsets.</summary>
    public string Raw { get; }

    /// <summary>Object number → dictionary (or whole body for non-streams).</summary>
    public Dictionary<int, string> Objects { get; } = [];

    /// <summary>Object number → decoded stream data.</summary>
    public Dictionary<int, byte[]> Streams { get; } = [];

    /// <summary>Byte offset of each object's "N 0 obj" line.</summary>
    public Dictionary<int, int> Offsets { get; } = [];

    /// <summary>Page dictionaries' object numbers, in page order.</summary>
    public List<int> PageIds { get; } = [];

    public int PageCount => PageIds.Count;

    public static PdfDoc Render(string markdown, string? baseDir = null, string title = "Test", TextWriter? warnings = null, Paper? paper = null, RenderOptions? options = null)
    {
        var renderer = new Renderer(baseDir ?? Path.GetTempPath(), warnings ?? TextWriter.Null, options ?? new RenderOptions(paper ?? Paper.A4));
        renderer.Render(Markdown.Parse(markdown));
        using var ms = new MemoryStream();
        renderer.Save(ms, title);
        return Parse(ms.ToArray());
    }

    /// <summary>Saves a writer with a placeholder catalog and info so its objects can be inspected.</summary>
    public static PdfDoc From(PdfWriter pdf)
    {
        var root = pdf.Add("<< /Type /Catalog >>");
        var info = pdf.Add("<< >>");
        using var ms = new MemoryStream();
        pdf.Save(ms, root, info);
        return Parse(ms.ToArray());
    }

    public static PdfDoc Parse(byte[] bytes)
    {
        var doc = new PdfDoc(bytes);
        var raw = doc.Raw;
        foreach (Match m in ObjHeader().Matches(raw))
        {
            var id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            doc.Offsets[id] = m.Index;
            var start = m.Index + m.Length;
            var eol = raw.IndexOf('\n', start);
            if (string.CompareOrdinal(raw, eol + 1, "stream\n", 0, 7) == 0)
            {
                var dict = raw[start..eol];
                var length = int.Parse(LengthEntry().Match(dict).Groups[1].Value, CultureInfo.InvariantCulture);
                var data = bytes.AsSpan(eol + 8, length).ToArray();
                doc.Objects[id] = dict;
                doc.Streams[id] = dict.Contains("/FlateDecode", StringComparison.Ordinal) && !dict.Contains("/Predictor", StringComparison.Ordinal)
                    ? PdfWriter.Inflate(data)
                    : data;
            }
            else
            {
                doc.Objects[id] = raw[start..raw.IndexOf("\nendobj\n", start, StringComparison.Ordinal)];
            }
        }

        var pages = doc.Objects.Values.FirstOrDefault(o => o.Contains("/Type /Pages", StringComparison.Ordinal));
        if (pages is not null)
        {
            doc.PageIds.AddRange(Refs(Kids().Match(pages).Groups[1].Value));
        }

        return doc;
    }

    public static List<int> Refs(string text) =>
        [.. Ref().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

    /// <summary>The decoded content stream of a page (0-based).</summary>
    public string Content(int page)
    {
        var contents = int.Parse(ContentsEntry().Match(Objects[PageIds[page]]).Groups[1].Value, CultureInfo.InvariantCulture);
        return Encoding.Latin1.GetString(Streams[contents]);
    }

    public IEnumerable<string> Contents() => Enumerable.Range(0, PageCount).Select(Content);

    public List<TextRun> Runs(int page) =>
        [.. TextOp().Matches(Content(page)).Select(m => new TextRun(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
            Unescape(m.Groups[5].Value)))];

    public List<TextRun> AllRuns() => [.. Enumerable.Range(0, PageCount).SelectMany(Runs)];

    /// <summary>All text on all pages, one run per line.</summary>
    public string Text() => string.Join('\n', AllRuns().Select(r => r.Text));

    public IEnumerable<string> ObjectsContaining(string fragment) =>
        Objects.Values.Where(o => o.Contains(fragment, StringComparison.Ordinal));

    /// <summary>Decodes a PDF literal string body (without the parentheses) from WinAnsi.</summary>
    public static string Unescape(string literal)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\')
            {
                bytes.Add((byte)literal[i]);
                continue;
            }

            var octal = 0;
            var digits = 0;
            while (digits < 3 && i + 1 < literal.Length && literal[i + 1] is >= '0' and <= '7')
            {
                octal = (octal * 8) + (literal[++i] - '0');
                digits++;
            }

            bytes.Add(digits > 0 ? (byte)octal : (byte)literal[++i]);
        }

        return WinAnsi.GetString([.. bytes]);
    }

    /// <summary>Decodes a PDF text string written as <c>&lt;FEFF....&gt;</c>.</summary>
    public static string DecodeTextString(string hex) =>
        Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex.Trim('<', '>'))[2..]);

    private static Encoding CreateWinAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    [GeneratedRegex(@"(?m)^(\d+) 0 obj\n")]
    private static partial Regex ObjHeader();

    [GeneratedRegex(@"/Length (\d+)")]
    private static partial Regex LengthEntry();

    [GeneratedRegex(@"/Kids \[([^\]]*)\]")]
    private static partial Regex Kids();

    [GeneratedRegex(@"/Contents (\d+) 0 R")]
    private static partial Regex ContentsEntry();

    [GeneratedRegex(@"(\d+) 0 R")]
    private static partial Regex Ref();

    [GeneratedRegex(@"BT /F(\d) ([\d.]+) Tf [\d. ]+ rg (-?[\d.]+) (-?[\d.]+) Td \(((?:\\[0-7]{1,3}|\\.|[^\\)])*)\) Tj ET")]
    private static partial Regex TextOp();
}
