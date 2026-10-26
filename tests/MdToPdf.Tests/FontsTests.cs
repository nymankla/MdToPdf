namespace MdToPdf.Tests;

public sealed class FontsTests
{
    [Theory]
    [InlineData('A', 65)]
    [InlineData('~', 126)]
    [InlineData(' ', 0xA0)]
    [InlineData('é', 0xE9)]
    [InlineData('ÿ', 0xFF)]
    [InlineData('€', 128)]
    [InlineData('“', 147)]
    [InlineData('™', 153)]
    [InlineData('→', 63)]
    [InlineData('\t', 63)]
    [InlineData('Ω', 63)]
    public void EncodesToWinAnsi(char c, int expected) => Assert.Equal(expected, Fonts.Encode(c));

    [Fact]
    public void OneBaseNamePerFont() => Assert.Equal(Enum.GetValues<FontId>().Length, Fonts.BaseNames.Length);

    [Fact]
    public void MonoIsFixedPitch() => Assert.Equal(18, Fonts.Width("abc", FontId.Mono, 10), 6);

    [Fact]
    public void HelveticaWidthsComeFromAfm()
    {
        Assert.Equal((667 + 556) * 10 / 1000.0, Fonts.Width("Ab", FontId.Regular, 10), 6);
        Assert.Equal((722 + 611) * 10 / 1000.0, Fonts.Width("Ab", FontId.Bold, 10), 6);
    }

    [Fact]
    public void ObliqueSharesUprightWidths()
    {
        Assert.Equal(Fonts.Width("Hello", FontId.Regular, 12), Fonts.Width("Hello", FontId.Italic, 12));
        Assert.Equal(Fonts.Width("Hello", FontId.Bold, 12), Fonts.Width("Hello", FontId.BoldItalic, 12));
    }

    [Fact]
    public void UnencodableCharMeasuresAsQuestionMark() =>
        Assert.Equal(Fonts.Width("?", FontId.Regular, 10), Fonts.Width("Ω", FontId.Regular, 10));

    [Fact]
    public void WidthScalesWithSize() =>
        Assert.Equal(2 * Fonts.Width("Width", FontId.Regular, 10), Fonts.Width("Width", FontId.Regular, 20), 6);

    [Fact]
    public void EmptyStringHasNoWidth() => Assert.Equal(0, Fonts.Width("", FontId.Regular, 10));
}
