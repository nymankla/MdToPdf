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
            WriteText(PdfSyntax.Invariant($"{k + 1} 0 obj\n"));
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

    /// <exception cref="InvalidDataException">The data isn't zlib, or unpacks to more than <paramref name="maxBytes"/>.</exception>
    public static byte[] Inflate(byte[] data, long maxBytes = long.MaxValue)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = z.Read(buffer)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                throw new InvalidDataException();
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }
}
