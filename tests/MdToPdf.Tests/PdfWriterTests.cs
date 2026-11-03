using System.Globalization;
using System.Text;
using MdToPdf.Tests.Support;

namespace MdToPdf.Tests;

public sealed class PdfWriterTests
{
    [Fact]
    public void ObjectNumbersStartAtOne()
    {
        var pdf = new PdfWriter();
        Assert.Equal(1, pdf.Add("<< >>"));
        Assert.Equal(2, pdf.Reserve());
        Assert.Equal(3, pdf.AddStream("", [1, 2, 3], compress: false));
    }

    [Fact]
    public void ReservedObjectIsFilledBySet()
    {
        var pdf = new PdfWriter();
        var id = pdf.Reserve();
        pdf.Set(id, "<< /Late true >>");
        Assert.Equal("<< /Late true >>", PdfDoc.From(pdf).Objects[id]);
    }

    [Fact]
    public void SaveRejectsUnsetReservation()
    {
        var pdf = new PdfWriter();
        var id = pdf.Reserve();
        var e = Assert.Throws<InvalidOperationException>(() => pdf.Save(Stream.Null, id, id));
        Assert.Contains("1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileStructureIsValid()
    {
        var pdf = new PdfWriter();
        pdf.Add("<< /A 1 >>");
        pdf.AddStream("/B 2", Encoding.ASCII.GetBytes("endobj inside a stream"), compress: false);
        pdf.AddStream("", Encoding.ASCII.GetBytes("compressed"), compress: true);
        var doc = PdfDoc.From(pdf);
        var raw = doc.Raw;

        Assert.StartsWith("%PDF-1.7\n", raw, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", raw, StringComparison.Ordinal);
        Assert.Contains("/Size 6 /Root 4 0 R /Info 5 0 R", raw, StringComparison.Ordinal);

        // startxref points at the xref table, and every entry points at its object.
        var startxref = int.Parse(raw[(raw.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], CultureInfo.InvariantCulture);
        Assert.Equal("xref\n0 6\n", raw.Substring(startxref, 9));
        var entries = raw[(startxref + 9)..].Split('\n').Skip(1).Take(5).ToList();
        for (var k = 0; k < entries.Count; k++)
        {
            Assert.Matches(@"^\d{10} 00000 n $", entries[k]);
            var offset = int.Parse(entries[k][..10], CultureInfo.InvariantCulture);
            Assert.Equal(doc.Offsets[k + 1], offset);
        }
    }

    [Fact]
    public void UncompressedStreamHasExactLength()
    {
        var pdf = new PdfWriter();
        var id = pdf.AddStream("/Type /Test", [10, 20, 30, 40], compress: false);
        var doc = PdfDoc.From(pdf);
        Assert.Equal("<< /Type /Test /Length 4 >>", doc.Objects[id]);
        Assert.Equal([10, 20, 30, 40], doc.Streams[id]);
    }

    [Fact]
    public void CompressedStreamRoundTrips()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BT ET\n", 200)));
        var pdf = new PdfWriter();
        var id = pdf.AddStream("", data, compress: true);
        var doc = PdfDoc.From(pdf);
        Assert.Contains("/Filter /FlateDecode", doc.Objects[id], StringComparison.Ordinal);
        Assert.Equal(data, doc.Streams[id]);
    }

    [Fact]
    public void DeflateInflateRoundTrip()
    {
        byte[] data = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))];
        Assert.Equal(data, PdfWriter.Inflate(PdfWriter.Deflate(data)));
        Assert.Empty(PdfWriter.Inflate(PdfWriter.Deflate([])));
    }

    [Fact]
    public void InflateStopsAtTheLimit()
    {
        var bomb = PdfWriter.Deflate(new byte[100_000]);
        Assert.Equal(100_000, PdfWriter.Inflate(bomb, 100_000).Length);
        Assert.Throws<InvalidDataException>(() => PdfWriter.Inflate(bomb, 99_999));
    }
}
