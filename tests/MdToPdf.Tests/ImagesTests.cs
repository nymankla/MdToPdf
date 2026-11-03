using MdToPdf.Tests.Support;

namespace MdToPdf.Tests;

public sealed class ImagesTests
{
    private static (PdfImage? Image, PdfDoc Doc) Embed(byte[] data)
    {
        var pdf = new PdfWriter();
        var img = Images.Embed(pdf, data);
        return (img, PdfDoc.From(pdf));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' })]
    public void UnsupportedFormatIsNull(byte[] data) => Assert.Null(Embed(data).Image);

    // ---------------------------------------------------------------- JPEG

    [Theory]
    [InlineData(3, "/ColorSpace /DeviceRGB ")]
    [InlineData(1, "/ColorSpace /DeviceGray ")]
    [InlineData(4, "/ColorSpace /DeviceCMYK /Decode [1 0 1 0 1 0 1 0] ")]
    public void JpegPassesThroughWithDimensions(byte components, string colorSpace)
    {
        var jpeg = ImageBuilder.Jpeg(320, 200, components);
        var (img, doc) = Embed(jpeg);

        Assert.NotNull(img);
        Assert.Equal((320, 200), (img.Width, img.Height));
        var dict = doc.Objects[img.Id];
        Assert.Contains("/Width 320 /Height 200 ", dict, StringComparison.Ordinal);
        Assert.Contains(colorSpace, dict, StringComparison.Ordinal);
        Assert.Contains("/Filter /DCTDecode", dict, StringComparison.Ordinal);
        Assert.Equal(jpeg, doc.Streams[img.Id]);
    }

    [Theory]
    [InlineData(0xC2)] // progressive
    [InlineData(0xC1)]
    public void JpegAcceptsOtherStartOfFrameMarkers(byte marker) =>
        Assert.Equal(64, Embed(ImageBuilder.Jpeg(64, 32, 3, marker)).Image?.Width);

    [Fact]
    public void JpegSkipsStandaloneMarkers() => Assert.Equal(16, Embed(ImageBuilder.Jpeg(16, 8, 3, withRestartMarker: true)).Image?.Width);

    [Fact]
    public void JpegWithoutFrameIsNull() => Assert.Null(Embed([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0, 0, 0xFF, 0xD9, 0, 0]).Image);

    [Fact]
    public void JpegHuffmanTableIsNotAFrame()
    {
        // A DHT (C4) segment whose bytes would decode as bogus dimensions if mistaken for SOF.
        byte[] dht = [0xFF, 0xC4, 0x00, 0x08, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F];
        var jpeg = ImageBuilder.Jpeg(10, 20, 3);
        byte[] data = [.. jpeg[..2], .. dht, .. jpeg[2..]];
        Assert.Equal((10, 20), Embed(data).Image is { } i ? (i.Width, i.Height) : default);
    }

    // ---------------------------------------------------------------- PNG pass-through

    [Theory]
    [InlineData(2, 3, "/DeviceRGB")]
    [InlineData(0, 1, "/DeviceGray")]
    public void OpaquePngPassesThroughWithPredictor(byte type, int colors, string colorSpace)
    {
        const int W = 3, H = 2;
        var raw = new byte[W * H * colors];
        Random.Shared.NextBytes(raw);
        var png = ImageBuilder.Png(W, H, 8, type, ImageBuilder.Filter(raw, W, H, colors, [1, 2]));
        var (img, doc) = Embed(png);

        Assert.NotNull(img);
        var dict = doc.Objects[img.Id];
        Assert.Contains($"/ColorSpace {colorSpace} ", dict, StringComparison.Ordinal);
        Assert.Contains($"/DecodeParms << /Predictor 15 /Colors {colors} /BitsPerComponent 8 /Columns {W} >>", dict, StringComparison.Ordinal);
        Assert.DoesNotContain("/SMask", dict, StringComparison.Ordinal);
    }

    [Fact]
    public void PalettePngUsesIndexedColorSpace()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        var png = ImageBuilder.Png(2, 1, 8, 3, ImageBuilder.Unfiltered([0, 2], 2, 1, 1), palette);
        var (img, doc) = Embed(png);

        Assert.NotNull(img);
        Assert.Contains("/ColorSpace [/Indexed /DeviceRGB 2 <FF000000FF000000FF>]", doc.Objects[img.Id], StringComparison.Ordinal);
    }

    [Fact]
    public void InterlacedPngIsNull() =>
        Assert.Null(Embed(ImageBuilder.Png(1, 1, 8, 2, ImageBuilder.Unfiltered([1, 2, 3], 1, 1, 3), interlace: 1)).Image);

    [Fact]
    public void PngWithZeroSizeIsNull() => Assert.Null(Embed(ImageBuilder.Png(0, 0, 8, 2, [])).Image);

    // ---------------------------------------------------------------- PNG with alpha

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void RgbaPngIsUnfilteredAndSplitIntoSoftMask(byte filter)
    {
        const int W = 4, H = 3;
        var raw = new byte[W * H * 4];
        new Random(filter).NextBytes(raw);
        var png = ImageBuilder.Png(W, H, 8, 6, ImageBuilder.Filter(raw, W, H, 4, [filter, filter, filter]));
        var (img, doc) = Embed(png);

        Assert.NotNull(img);
        var dict = doc.Objects[img.Id];
        Assert.Contains("/ColorSpace /DeviceRGB ", dict, StringComparison.Ordinal);
        var mask = PdfDoc.Refs(dict[dict.IndexOf("/SMask", StringComparison.Ordinal)..])[0];
        Assert.Contains("/ColorSpace /DeviceGray /BitsPerComponent 8", doc.Objects[mask], StringComparison.Ordinal);

        Assert.Equal(raw.Chunk(4).SelectMany(p => p[..3]), doc.Streams[img.Id]);
        Assert.Equal(raw.Chunk(4).Select(p => p[3]), doc.Streams[mask]);
    }

    [Fact]
    public void MixedRowFiltersDecodeCorrectly()
    {
        const int W = 5, H = 5;
        var raw = new byte[W * H * 4];
        new Random(42).NextBytes(raw);
        var (img, doc) = Embed(ImageBuilder.Png(W, H, 8, 6, ImageBuilder.Filter(raw, W, H, 4, [4, 3, 2, 1, 0])));
        Assert.NotNull(img);
        Assert.Equal(raw.Chunk(4).SelectMany(p => p[..3]), doc.Streams[img.Id]);
    }

    [Fact]
    public void GrayAlphaPngSplitsIntoGrayAndMask()
    {
        byte[] raw = [10, 200, 20, 100, 30, 0];
        var (img, doc) = Embed(ImageBuilder.Png(3, 1, 8, 4, ImageBuilder.Unfiltered(raw, 3, 1, 2)));

        Assert.NotNull(img);
        Assert.Contains("/ColorSpace /DeviceGray ", doc.Objects[img.Id], StringComparison.Ordinal);
        Assert.Equal([10, 20, 30], doc.Streams[img.Id]);
        var mask = PdfDoc.Refs(doc.Objects[img.Id][doc.Objects[img.Id].IndexOf("/SMask", StringComparison.Ordinal)..])[0];
        Assert.Equal([200, 100, 0], doc.Streams[mask]);
    }

    [Fact]
    public void SixteenBitRgbaKeepsTwoBytesPerSample()
    {
        byte[] raw = [1, 2, 3, 4, 5, 6, 7, 8];
        var (img, doc) = Embed(ImageBuilder.Png(1, 1, 16, 6, ImageBuilder.Unfiltered(raw, 1, 1, 8)));

        Assert.NotNull(img);
        Assert.Contains("/BitsPerComponent 16", doc.Objects[img.Id], StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4, 5, 6], doc.Streams[img.Id]);
    }

    [Fact]
    public void LowBitDepthAlphaPngIsNull() => Assert.Null(Embed(ImageBuilder.Png(1, 1, 4, 6, [0, 0])).Image);

    // ---------------------------------------------------------------- real files

    [Theory]
    [InlineData("alpha.png")]
    [InlineData("gradient.jpg")]
    public void SampleImagesEmbed(string name)
    {
        var img = Embed(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", name))).Image;
        Assert.NotNull(img);
        Assert.True(img.Width > 0 && img.Height > 0);
    }

    // ---------------------------------------------------------------- limits

    [Fact]
    public void HugeDecodedImageIsRefusedWithoutAllocating()
    {
        var png = ImageBuilder.Png(30000, 30000, 8, 6, new byte[16]);
        Assert.Null(Embed(png).Image);
    }

    [Fact]
    public void HugePaletteMaskIsRefused()
    {
        var png = ImageBuilder.Png(60000, 60000, 8, 3, new byte[16], palette: [1, 2, 3], transparency: [0]);
        Assert.Null(Embed(png).Image);
    }

    [Fact]
    public void DataInflatingBeyondTheImageSizeIsCorrupt()
    {
        var png = ImageBuilder.Png(1, 1, 8, 6, new byte[100_000]);
        Assert.Throws<InvalidDataException>(() => Embed(png));
    }

    [Theory]
    [InlineData(5)] // no such color type
    [InlineData(7)]
    public void UnknownColorTypeIsUnsupported(byte type) => Assert.Null(Embed(ImageBuilder.Png(1, 1, 8, type, [0, 0])).Image);

    [Fact]
    public void PaletteImageWithoutPaletteIsUnsupported() => Assert.Null(Embed(ImageBuilder.Png(1, 1, 8, 3, [0, 0])).Image);

    // ---------------------------------------------------------------- PNG transparency

    [Fact]
    public void PaletteTransparencyBecomesASoftMask()
    {
        var png = ImageBuilder.Png(2, 1, 8, 3, [0, 0, 1], palette: [1, 2, 3, 4, 5, 6], transparency: [0]);
        var (img, doc) = Embed(png);
        var dict = doc.Objects[img!.Id];
        Assert.Contains("/Predictor 15", dict, StringComparison.Ordinal);
        var mask = PdfDoc.Refs(dict[dict.IndexOf("/SMask", StringComparison.Ordinal)..])[0];
        Assert.Equal(new byte[] { 0, 255 }, doc.Streams[mask]); // entry 0 is transparent, entry 1 has no alpha listed
    }

    [Fact]
    public void PaletteTransparencyHandlesPackedPixels()
    {
        // Two bits per pixel: indexes 0, 1, 2, 3 in one byte.
        var png = ImageBuilder.Png(4, 1, 2, 3, [0, 0b00_01_10_11], palette: new byte[12], transparency: [10, 20, 30]);
        var (img, doc) = Embed(png);
        var dict = doc.Objects[img!.Id];
        var mask = PdfDoc.Refs(dict[dict.IndexOf("/SMask", StringComparison.Ordinal)..])[0];
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, doc.Streams[mask]);
    }

    [Fact]
    public void PaletteTransparencyUndoesRowFilters()
    {
        var png = ImageBuilder.Png(2, 2, 8, 3, ImageBuilder.Filter([0, 1, 1, 0], 2, 2, 1, [1, 2]), palette: new byte[6], transparency: [50]);
        var (img, doc) = Embed(png);
        var dict = doc.Objects[img!.Id];
        var mask = PdfDoc.Refs(dict[dict.IndexOf("/SMask", StringComparison.Ordinal)..])[0];
        Assert.Equal(new byte[] { 50, 255, 255, 50 }, doc.Streams[mask]);
    }

    [Fact]
    public void GrayTransparencyBecomesAColorKey()
    {
        var png = ImageBuilder.Png(1, 1, 8, 0, [0, 7], transparency: [0, 7]);
        var (img, doc) = Embed(png);
        Assert.Contains("/Mask [7 7]", doc.Objects[img!.Id], StringComparison.Ordinal);
    }

    [Fact]
    public void RgbTransparencyBecomesAColorKey()
    {
        var png = ImageBuilder.Png(1, 1, 8, 2, [0, 1, 2, 3], transparency: [0, 1, 0, 2, 0, 3]);
        var (img, doc) = Embed(png);
        Assert.Contains("/Mask [1 1 2 2 3 3]", doc.Objects[img!.Id], StringComparison.Ordinal);
    }

    [Fact]
    public void OpaquePngHasNoMask()
    {
        var (img, doc) = Embed(ImageBuilder.Png(1, 1, 8, 2, [0, 1, 2, 3]));
        Assert.DoesNotContain("Mask", doc.Objects[img!.Id], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- JPEG orientation

    [Theory]
    [InlineData(1, 320, 200, false)]
    [InlineData(1, 320, 200, true)]
    [InlineData(3, 320, 200, false)]
    [InlineData(6, 200, 320, false)]
    [InlineData(6, 200, 320, true)]
    [InlineData(8, 200, 320, true)]
    public void ExifOrientationSetsTheDisplayedSize(ushort orientation, int w, int h, bool bigEndian)
    {
        var (img, doc) = Embed(ImageBuilder.Jpeg(320, 200, 3, exif: ImageBuilder.ExifOrientation(orientation, bigEndian)));
        Assert.Equal((w, h, (int)orientation), (img!.Width, img.Height, img.Orientation));
        Assert.Contains("/Width 320 /Height 200", doc.Objects[img.Id], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "10 0 0 20 1 2")]
    [InlineData(2, "-10 0 0 20 11 2")]
    [InlineData(3, "-10 0 0 -20 11 22")]
    [InlineData(4, "10 0 0 -20 1 22")]
    [InlineData(5, "0 -20 -10 0 11 22")]
    [InlineData(6, "0 -20 10 0 1 22")]
    [InlineData(7, "0 20 10 0 1 2")]
    [InlineData(8, "0 20 -10 0 11 2")]
    public void MatrixFillsTheBox(int orientation, string matrix) =>
        Assert.Equal(matrix, new PdfImage(1, 10, 20, orientation).Matrix(1, 2, 10, 20));

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void InvalidOrientationIsIgnored(ushort orientation)
    {
        var (img, _) = Embed(ImageBuilder.Jpeg(320, 200, 3, exif: ImageBuilder.ExifOrientation(orientation, false)));
        Assert.Equal(1, img!.Orientation);
    }

    [Fact]
    public void TruncatedExifIsIgnored()
    {
        var exif = ImageBuilder.ExifOrientation(6, false)[..16];
        Assert.Equal(1, Embed(ImageBuilder.Jpeg(320, 200, 3, exif: exif)).Image!.Orientation);
    }

    [Fact]
    public void OtherApp1DataIsIgnored()
    {
        byte[] xmp = [.. "http://ns.adobe.com/xap/1.0/\0"u8, 1, 2, 3];
        Assert.Equal(1, Embed(ImageBuilder.Jpeg(320, 200, 3, exif: xmp)).Image!.Orientation);
    }

    [Theory]
    [InlineData(300, 800, 225, 600)]
    [InlineData(2000, 100, 483.28, 24.16)]
    [InlineData(100, 4000, 18.25, 730)]
    public void FitShrinksButNeverEnlarges(int w, int h, double fw, double fh)
    {
        var (width, height) = new PdfImage(1, w, h).Fit(483.28, 730);
        Assert.Equal(fw, width, 1);
        Assert.Equal(fh, height, 1);
    }
}
