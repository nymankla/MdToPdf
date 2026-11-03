using System.Globalization;

namespace MdToPdf.Tests;

public sealed class PdfSyntaxTests
{
    /// <summary>A culture writing "1,5": what a Swedish machine would do to PDF syntax if a number slipped through unformatted.</summary>
    internal static CultureInfo CommaCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }

    [Theory]
    [InlineData(2.0, "2")]
    [InlineData(1.5, "1.5")]
    [InlineData(1.234, "1.23")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(0.001, "0")]
    public void FormatsNumbersWithTwoDecimals(double value, string expected) => Assert.Equal(expected, PdfSyntax.N(value));

    [Theory]
    [InlineData("plain", "(plain)")]
    [InlineData(@"a(b)c\", @"(a\(b\)c\\)")]
    [InlineData("é", @"(\351)")]
    [InlineData("€", @"(\200)")]
    [InlineData("→", "(?)")]
    [InlineData("", "()")]
    public void Literal(string text, string expected) => Assert.Equal(expected, PdfSyntax.Literal(text));

    [Theory]
    [InlineData("", "<FEFF>")]
    [InlineData("Aé", "<FEFF004100E9>")]
    [InlineData("→", "<FEFF2192>")]
    public void TextString(string text, string expected) => Assert.Equal(expected, PdfSyntax.TextString(text));

    [Fact]
    public void RefsAreSpaceSeparated() => Assert.Equal("3 0 R 12 0 R", PdfSyntax.Refs([3, 12]));

    [Fact]
    public void InvariantIgnoresCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CommaCulture();
            Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture));
            Assert.Equal("1.5", PdfSyntax.Invariant($"{1.5}"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
