using System.Buffers.Binary;
using System.Text;
using static MdToPdf.PdfSyntax;

namespace MdToPdf;

/// <summary>An embedded image. The size is in pixels as displayed, so after any rotation by its EXIF orientation (1 is upright).</summary>
internal sealed record PdfImage(int Id, int Width, int Height, int Orientation = 1)
{
    /// <summary>The placed size: 96 dpi, shrunk to fit the given box.</summary>
    public (double Width, double Height) Fit(double maxWidth, double maxHeight)
    {
        double w = Width * 0.75, h = Height * 0.75;
        var scale = Math.Min(1, Math.Min(maxWidth / w, maxHeight / h));
        return (w * scale, h * scale);
    }

    /// <summary>The operands of <c>cm</c> that map the image's unit square onto the given box, undoing the EXIF orientation.</summary>
    public string Matrix(double x, double y, double w, double h) => Orientation switch
    {
        2 => $"{N(-w)} 0 0 {N(h)} {N(x + w)} {N(y)}",
        3 => $"{N(-w)} 0 0 {N(-h)} {N(x + w)} {N(y + h)}",
        4 => $"{N(w)} 0 0 {N(-h)} {N(x)} {N(y + h)}",
        5 => $"0 {N(-h)} {N(-w)} 0 {N(x + w)} {N(y + h)}",
        6 => $"0 {N(-h)} {N(w)} 0 {N(x)} {N(y + h)}",
        7 => $"0 {N(h)} {N(w)} 0 {N(x)} {N(y)}",
        8 => $"0 {N(h)} {N(-w)} 0 {N(x + w)} {N(y)}",
        _ => $"{N(w)} 0 0 {N(h)} {N(x)} {N(y)}",
    };
}

/// <summary>Embeds JPEG (passed through as-is) and PNG (passed through, or decoded when it has transparency).</summary>
internal static class Images
{
    private const int ExifOrientationTag = 0x0112;

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

    private static string ImageDict(int w, int h, string colorSpace, int bits) =>
        Invariant($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace {colorSpace} /BitsPerComponent {bits}");

    private static PdfImage? Jpeg(PdfWriter pdf, byte[] d)
    {
        var orientation = 1;
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
                var id = pdf.AddStream(ImageDict(w, h, colorSpace, 8) + " /Filter /DCTDecode", d, compress: false);
                return orientation >= 5 ? new PdfImage(id, h, w, orientation) : new PdfImage(id, w, h, orientation);
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
            if (marker == 0xE1 && i + 2 + length <= d.Length && length >= 2)
            {
                orientation = ExifOrientation(d.AsSpan(i + 4, length - 2)) ?? orientation;
            }

            i += 2 + length;
        }

        return null;
    }

    /// <summary>Reads the orientation tag of an APP1 segment; null when it isn't valid EXIF. A photo with broken EXIF is still a photo.</summary>
    private static int? ExifOrientation(ReadOnlySpan<byte> app1)
    {
        if (app1.Length < 14 || !app1.StartsWith("Exif\0\0"u8))
        {
            return null;
        }

        var tiff = app1[6..];
        var bigEndian = tiff.StartsWith("MM"u8);
        int Read16(ReadOnlySpan<byte> s) => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

        var ifd = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(tiff[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]);
        if (ifd + 2 > (uint)tiff.Length)
        {
            return null;
        }

        var entries = tiff[(int)ifd..];
        var count = Read16(entries);
        for (var k = 0; k < count && 2 + ((k + 1) * 12) <= entries.Length; k++)
        {
            var entry = entries.Slice(2 + (k * 12), 12);
            if (Read16(entry) == ExifOrientationTag)
            {
                return Read16(entry[8..]) is >= 1 and <= 8 and var value ? value : null;
            }
        }

        return null;
    }

    private static PdfImage? Png(PdfWriter pdf, byte[] d)
    {
        int w = 0, h = 0, depth = 0, type = 0, interlace = 0;
        var palette = Array.Empty<byte>();
        var transparency = Array.Empty<byte>();
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
                case "tRNS":
                    transparency = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                default:
                    break;
            }

            i += 12 + len;
        }

        var valid = w > 0 && h > 0 && interlace == 0 && type is 0 or 2 or 3 or 4 or 6
            && !(type is 4 or 6 && depth < 8) && !(type == 3 && palette.Length == 0);
        if (!valid)
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
        var head = ImageDict(w, h, colorSpace, depth);

        if (type is 0 or 2 or 3)
        {
            // PDF's Flate predictor 15 understands PNG row filters, so the data passes through untouched.
            var parms = Invariant($" /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors {colors} /BitsPerComponent {depth} /Columns {w} >>");
            var mask = type == 3 ? PaletteMask(pdf, idat, transparency, w, h, depth) : ColorKeyMask(transparency, colors, depth);
            return mask is null ? null : new PdfImage(pdf.AddStream(head + parms + mask, idat.ToArray(), compress: false), w, h);
        }

        // Gray+alpha or RGBA: undo the row filters and split alpha into a soft mask.
        var sample = depth / 8;
        var pixel = (colors + 1) * sample;
        if (!FitsInMemory(w, h, pixel))
        {
            return null;
        }

        var raw = Unfilter(PdfWriter.Inflate(idat.ToArray(), (((long)w * pixel) + 1) * h), w * pixel, h, pixel);
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

        var soft = pdf.AddStream(ImageDict(w, h, "/DeviceGray", depth), alpha, compress: true);
        return new PdfImage(pdf.AddStream($"{head} /SMask {Ref(soft)}", color, compress: true), w, h);
    }

    private static bool FitsInMemory(int w, int h, int pixel) => (long)w * h * pixel <= Limits.MaxDecodedImageBytes;

    /// <summary>The tRNS chunk of a gray or RGB PNG names one color as transparent, which PDF's color key mask expresses directly.</summary>
    private static string ColorKeyMask(byte[] transparency, int colors, int depth)
    {
        if (transparency.Length < colors * 2)
        {
            return "";
        }

        var ranges = Enumerable.Range(0, colors).Select(c =>
        {
            int value = BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(c * 2));
            value &= (1 << depth) - 1;
            return Invariant($"{value} {value}");
        });
        return $" /Mask [{string.Join(' ', ranges)}]";
    }

    /// <summary>The tRNS chunk of a palette PNG gives an alpha per palette entry, which takes a soft mask built from the pixels.
    /// Returns the dictionary entry to add, or null when the image is too big to decode.</summary>
    private static string? PaletteMask(PdfWriter pdf, MemoryStream idat, byte[] transparency, int w, int h, int depth)
    {
        if (transparency.Length == 0)
        {
            return "";
        }

        if (!FitsInMemory(w, h, 1))
        {
            return null;
        }

        var stride = (int)((((long)w * depth) + 7) / 8);
        var raw = Unfilter(PdfWriter.Inflate(idat.ToArray(), ((long)stride + 1) * h), stride, h, 1);
        var alpha = new byte[w * h];
        for (var r = 0; r < h; r++)
        {
            for (var c = 0; c < w; c++)
            {
                var bit = c * depth;
                var index = (raw[(r * stride) + (bit / 8)] >> (8 - depth - (bit % 8))) & ((1 << depth) - 1);
                alpha[(r * w) + c] = index < transparency.Length ? transparency[index] : (byte)255;
            }
        }

        return $" /SMask {Ref(pdf.AddStream(ImageDict(w, h, "/DeviceGray", 8), alpha, compress: true))}";
    }

    private static byte[] Unfilter(byte[] data, int stride, int h, int pixel)
    {
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
