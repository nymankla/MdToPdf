using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MdToPdf.Tests.Support;

/// <summary>Builds minimal PNG and JPEG files for the image tests.</summary>
internal static class ImageBuilder
{
    /// <summary>A PNG with the given (already filtered) scanlines. CRCs are zero: the reader doesn't check them.</summary>
    public static byte[] Png(int w, int h, byte depth, byte type, byte[] scanlines, byte[]? palette = null, byte interlace = 0, byte[]? transparency = null)
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        (ihdr[8], ihdr[9], ihdr[12]) = (depth, type, interlace);
        Chunk(ms, "IHDR", ihdr);
        if (palette is not null)
        {
            Chunk(ms, "PLTE", palette);
        }

        if (transparency is not null)
        {
            Chunk(ms, "tRNS", transparency);
        }

        // Split IDAT in two to check that the reader concatenates chunks.
        var z = Zlib(scanlines);
        Chunk(ms, "IDAT", z.AsSpan(0, z.Length / 2));
        Chunk(ms, "IDAT", z.AsSpan(z.Length / 2));
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>Applies PNG row filters to raw pixel rows; <paramref name="filters"/> gives the filter type per row.</summary>
    public static byte[] Filter(byte[] raw, int w, int h, int pixel, byte[] filters)
    {
        var stride = w * pixel;
        var output = new byte[h * (stride + 1)];
        for (var r = 0; r < h; r++)
        {
            var o = r * stride;
            var dst = r * (stride + 1);
            output[dst] = filters[r];
            for (var i = 0; i < stride; i++)
            {
                int a = i >= pixel ? raw[o + i - pixel] : 0;
                int b = r > 0 ? raw[o - stride + i] : 0;
                int c = i >= pixel && r > 0 ? raw[o - stride + i - pixel] : 0;
                var predictor = filters[r] switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                output[dst + 1 + i] = (byte)(raw[o + i] - predictor);
            }
        }

        return output;
    }

    /// <summary>Raw rows with filter type 0 (none) prepended to each.</summary>
    public static byte[] Unfiltered(byte[] raw, int w, int h, int pixel) => Filter(raw, w, h, pixel, new byte[h]);

    /// <summary>A JPEG header (SOI, APP0, optional RST, SOF0, EOI) — enough for dimension sniffing.</summary>
    public static byte[] Jpeg(int w, int h, byte components, byte sofMarker = 0xC0, bool withRestartMarker = false, byte[]? exif = null)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xE0, 0x00, 0x10, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (exif is not null)
        {
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(exif.Length + 2));
            ms.Write([0xFF, 0xE1]);
            ms.Write(length);
            ms.Write(exif);
        }

        if (withRestartMarker)
        {
            ms.Write([0xFF, 0xD0]);
        }

        var sof = new byte[10 + (3 * components)];
        (sof[0], sof[1]) = (0xFF, sofMarker);
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(2), (ushort)(8 + (3 * components)));
        sof[4] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(5), (ushort)h);
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(7), (ushort)w);
        sof[9] = components;
        ms.Write(sof);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    /// <summary>An APP1 body whose only EXIF tag is the given orientation.</summary>
    public static byte[] ExifOrientation(ushort orientation, bool bigEndian)
    {
        void Write16(Span<byte> s, ushort v)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(s, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s, v);
            }
        }

        var tiff = new byte[8 + 2 + 12];
        (tiff[0], tiff[1]) = bigEndian ? ((byte)'M', (byte)'M') : ((byte)'I', (byte)'I');
        Write16(tiff.AsSpan(2), 42);
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        }

        Write16(tiff.AsSpan(8), 1);
        Write16(tiff.AsSpan(10), 0x0112);
        Write16(tiff.AsSpan(12), 3);
        Write16(tiff.AsSpan(18), orientation);
        return [.. "Exif\0\0"u8, .. tiff];
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        s.Write(len);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(body);
        s.Write([0, 0, 0, 0]);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
