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

    // ---------------------------------------------------------------- Prepare

    [Theory]
    [InlineData("a → b", "a -> b")]
    [InlineData("x ≤ y ≠ z", "x <= y != z")]
    [InlineData("done ✓", "done x")]
    [InlineData("x😀y", "x?y")]
    [InlineData("é € “q”", "é € “q”")]
    public void PrepareUsesAsciiStandIns(string text, string expected) => Assert.Equal(expected, Fonts.Prepare(text));

    [Fact]
    public void PrepareDropsInvisibleChars() =>
        Assert.Equal("ok x", Fonts.Prepare("ok ✔" + (char)0xFE0F + (char)0x200D + (char)0xFEFF));

    [Fact]
    public void PrepareTurnsThinSpaceIntoBreakableSpace() => Assert.Equal("a b", Fonts.Prepare("a" + (char)0x2009 + "b"));

    [Fact]
    public void PrepareReturnsAsciiUnchanged()
    {
        var text = "plain ASCII text";
        Assert.Same(text, Fonts.Prepare(text));
    }

    [Theory]
    [InlineData("a\u200Bb", "ab")] // zero-width space
    [InlineData("a\u200Cb", "ab")] // zero-width non-joiner
    [InlineData("a\u00ADb", "ab")] // soft hyphen
    [InlineData("a\u2060b", "ab")] // word joiner
    [InlineData("a\u0301", "a")] // an accent that didn't compose
    public void PrepareDropsInvisibleCharacters(string text, string expected) => Assert.Equal(expected, Fonts.Prepare(text));

    [Theory]
    [InlineData("a\u2010b", "a-b")]
    [InlineData("a\u2011b", "a-b")]
    [InlineData("a\u2012b", "a-b")]
    [InlineData("a\u2015b", "a—b")]
    [InlineData("a\u2002b", "a b")]
    [InlineData("a\u2003b", "a b")]
    [InlineData("a\u3000b", "a b")]
    public void PrepareMapsDashesAndSpaces(string text, string expected) => Assert.Equal(expected, Fonts.Prepare(text));

    [Fact]
    public void PrepareReportsWhatItCouldNotShow()
    {
        var lost = new SortedSet<int>();
        Assert.Equal("? ? ok é →".Replace("→", "->", StringComparison.Ordinal), Fonts.Prepare("中 😀 ok é →", lost));
        Assert.Equal([0x4E2D, 0x1F600], lost);
    }

    [Fact]
    public void ALiteralQuestionMarkIsNotReported()
    {
        var lost = new SortedSet<int>();
        Fonts.Prepare("what? é", lost);
        Assert.Empty(lost);
    }

    [Fact]
    public void LoneSurrogateBecomesAQuestionMark() => Assert.Equal("a?b", Fonts.Prepare("a\uD83Db"));

    [Theory]
    [InlineData(0x41, 'A')]
    [InlineData(0xE5, 'å')]
    [InlineData(0x80, '€')]
    [InlineData(0x96, '–')]
    [InlineData(0x81, '\uFFFD')]
    public void Windows1252(byte code, char expected) => Assert.Equal(expected, Fonts.FromWindows1252(code));

    [Fact]
    public void Windows1252RoundTripsThroughEncode()
    {
        for (var b = 0x20; b <= 0xFF; b++)
        {
            var c = Fonts.FromWindows1252((byte)b);
            if (c != '\uFFFD' && b != 0x7F)
            {
                Assert.Equal(b, Fonts.Encode(c));
            }
        }
    }

    [Theory]
    [InlineData("A\u030Aa\u0308", "Åä")]
    [InlineData("plain", "plain")]
    [InlineData("q\u0301", "q\u0301")] // no such letter in WinAnsi: left for Prepare
    [InlineData("\u0301a", "\u0301a")]
    public void Compose(string text, string expected) => Assert.Equal(expected, Fonts.Compose(text));

    [Fact]
    public void BoldMonoIsFixedPitchToo() => Assert.Equal(18, Fonts.Width("abc", FontId.MonoBold, 10), 6);
}
