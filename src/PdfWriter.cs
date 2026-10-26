using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MdToPdf;

/// <summary>Minimal PDF object store and serializer (objects, streams, xref, trailer).</summary>
internal sealed class PdfWriter
{
    private readonly List<byte[]?> objects = [];

    /// <summary>Allocates an object number to be filled later with <see cref="Set"/>.</summary>
    public int Reserve()
    {
        objects.Add(null);
        return objects.Count;
    }

    public int Add(string body)
    {
        var id = Reserve();
        Set(id, body);
        return id;
    }

    public void Set(int id, string body) => objects[id - 1] = Encoding.Latin1.GetBytes(body);

    /// <summary>Adds a stream object; <paramref name="dict"/> holds the dictionary entries without the brackets.</summary>
    public int AddStream(string dict, byte[] data, bool compress)
    {
        if (compress)
        {
            data = Deflate(data);
            dict += " /Filter /FlateDecode";
        }

        using var ms = new MemoryStream();
        ms.Write(Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< {dict} /Length {data.Length} >>\nstream\n")));
        ms.Write(data);
        ms.Write("\nendstream"u8);

        var id = Reserve();
        objects[id - 1] = ms.ToArray();
        return id;
    }

    public void Save(Stream output, int root, int info)
    {
        long pos = 0;
        void Write(ReadOnlySpan<byte> bytes)
        {
            output.Write(bytes);
            pos += bytes.Length;
        }

        void WriteText(string text) => Write(Encoding.Latin1.GetBytes(text));

        WriteText("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[objects.Count];
        for (var k = 0; k < objects.Count; k++)
        {
            offsets[k] = pos;
            WriteText(Invariant($"{k + 1} 0 obj\n"));
            Write(objects[k] ?? throw new InvalidOperationException($"PDF object {k + 1} was reserved but never written."));
            WriteText("\nendobj\n");
        }

        var xref = pos;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root {root} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        WriteText(sb.ToString());
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    public static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>A PDF literal string in WinAnsi, kept 7-bit by octal-escaping high bytes.</summary>
    public static string Literal(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('(');
        foreach (var c in text)
        {
            var b = Fonts.Encode(c);
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                sb.Append('\\').Append((char)b);
            }
            else if (b > 126)
            {
                sb.Append('\\').Append(Convert.ToString(b, 8));
            }
            else
            {
                sb.Append((char)b);
            }
        }

        return sb.Append(')').ToString();
    }

    /// <summary>A PDF text string (UTF-16BE with BOM) for metadata and bookmarks.</summary>
    public static string TextString(string text)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (var c in text)
        {
            sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.Append('>').ToString();
    }

    public static string Invariant(FormattableString s) => FormattableString.Invariant(s);
}
