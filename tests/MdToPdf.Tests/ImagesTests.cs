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
}
