using System.Buffers.Binary;
using System.Text;
using static MdToPdf.PdfWriter;

namespace MdToPdf;

internal sealed record PdfImage(int Id, int Width, int Height);

/// <summary>Embeds JPEG (passed through as-is) and PNG (passed through, or decoded when it has alpha).</summary>
internal static class Images
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <returns>The image, or null when the format is unsupported.</returns>
    public static PdfImage? Embed(PdfWriter pdf, byte[] data)
    {
        if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return Jpeg(pdf, data);
        }

        return data.AsSpan().StartsWith(PngSignature) ? Png(pdf, data) : null;
    }

    private static PdfImage? Jpeg(PdfWriter pdf, byte[] d)
    {
        for (var i = 2; i + 9 < d.Length;)
        {
            if (d[i] != 0xFF || d[i + 1] == 0xFF)
            {
                i++;
                continue;
            }

            var marker = d[i + 1];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8))
            {
                i += 2;
                continue;
            }

            // Start-of-frame markers carry the dimensions (C4, C8 and CC are other tables).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                int h = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 5));
                int w = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 7));
                var colorSpace = d[i + 9] switch
                {
                    1 => "/DeviceGray",
                    4 => "/DeviceCMYK /Decode [1 0 1 0 1 0 1 0]", // Adobe CMYK JPEGs are stored inverted
                    _ => "/DeviceRGB",
                };
                var id = pdf.AddStream(Invariant($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode"), d, compress: false);
                return new PdfImage(id, w, h);
            }

            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
        }

        return null;
    }

    private static PdfImage? Png(PdfWriter pdf, byte[] d)
    {
        int w = 0, h = 0, depth = 0, type = 0, interlace = 0;
        var palette = Array.Empty<byte>();
        using var idat = new MemoryStream();

        for (var i = 8; i + 12 <= d.Length;)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(i));
            var body = d.AsSpan(i + 8, len);
            switch (Encoding.ASCII.GetString(d, i + 4, 4))
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadInt32BigEndian(body);
                    h = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    (depth, type, interlace) = (body[8], body[9], body[12]);
                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                default:
                    break;
            }

            i += 12 + len;
        }

        if (w <= 0 || h <= 0 || interlace != 0 || (type is 4 or 6 && depth < 8))
        {
            return null;
        }

        var colors = type is 2 or 6 ? 3 : 1;
        var colorSpace = type switch
        {
            3 => Invariant($"[/Indexed /DeviceRGB {(palette.Length / 3) - 1} <{Convert.ToHexString(palette)}>]"),
            0 or 4 => "/DeviceGray",
            _ => "/DeviceRGB",
        };
        var head = Invariant($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace {colorSpace} /BitsPerComponent {depth}");

        if (type is 0 or 2 or 3)
        {
            // PDF's Flate predictor 15 understands PNG row filters, so the data passes through untouched.
            var parms = Invariant($" /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors {colors} /BitsPerComponent {depth} /Columns {w} >>");
            return new PdfImage(pdf.AddStream(head + parms, idat.ToArray(), compress: false), w, h);
        }

        // Gray+alpha or RGBA: undo the row filters and split alpha into a soft mask.
        var sample = depth / 8;
        var pixel = (colors + 1) * sample;
        var raw = Unfilter(Inflate(idat.ToArray()), w, h, pixel);
        var color = new byte[w * h * colors * sample];
        var alpha = new byte[w * h * sample];
        for (int p = 0, ci = 0, ai = 0; p < raw.Length;)
        {
            for (var k = 0; k < colors * sample; k++)
            {
                color[ci++] = raw[p++];
            }

            for (var k = 0; k < sample; k++)
            {
                alpha[ai++] = raw[p++];
            }
        }

        var mask = pdf.AddStream(Invariant($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent {depth}"), alpha, compress: true);
        return new PdfImage(pdf.AddStream(Invariant($"{head} /SMask {mask} 0 R"), color, compress: true), w, h);
    }

    private static byte[] Unfilter(byte[] data, int w, int h, int pixel)
    {
        var stride = w * pixel;
        var result = new byte[h * stride];
        for (var r = 0; r < h; r++)
        {
            var filter = data[r * (stride + 1)];
            var src = (r * (stride + 1)) + 1;
            var o = r * stride;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= pixel ? result[o + i - pixel] : 0;
                int b = r > 0 ? result[o - stride + i] : 0;
                int c = i >= pixel && r > 0 ? result[o - stride + i - pixel] : 0;
                int x = data[src + i];
                result[o + i] = (byte)(filter switch
                {
                    1 => x + a,
                    2 => x + b,
                    3 => x + ((a + b) >> 1),
                    4 => x + Paeth(a, b, c),
                    _ => x,
                });
            }
        }

        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
