using MdToPdf.Tests.Support;

namespace MdToPdf.Tests;

public sealed class PdfAssemblerTests
{
    [Theory]
    [InlineData("other.md", "other.pdf")]
    [InlineData("sub/other.MD", "sub/other.pdf")]
    [InlineData("other.markdown", "other.pdf")]
    [InlineData("my%20notes.md", "my notes.pdf")]
    [InlineData("other.md#section", "other.pdf")]
    [InlineData("../up.md?x=1", "../up.pdf")]
    public void LinkToMarkdownFileBecomesItsPdf(string target, string path) => Assert.Equal(path, PdfAssembler.ConvertedPdfPath(target));

    [Theory]
    [InlineData("https://x.y/a.md")]
    [InlineData("mailto:a@b.md")]
    [InlineData("C:/docs/a.md")]
    [InlineData("/root/a.md")]
    [InlineData("other.txt")]
    [InlineData("")]
    public void OtherLinksStayAsTheyAre(string target) => Assert.Null(PdfAssembler.ConvertedPdfPath(target));

    [Fact]
    public void MarkdownLinkOpensTheOtherPdf()
    {
        var link = Assert.Single(PdfDoc.Render("[x](other.md)").ObjectsContaining("/Subtype /Link"));
        Assert.Contains("/S /GoToR", link, StringComparison.Ordinal);
        Assert.Contains("/F (other.pdf)", link, StringComparison.Ordinal);
        Assert.Contains($"/UF {PdfSyntax.TextString("other.pdf")}", link, StringComparison.Ordinal);
    }

    [Fact]
    public void WebLinkStaysAUri()
    {
        var link = Assert.Single(PdfDoc.Render("[x](https://x.y/a.md)").ObjectsContaining("/Subtype /Link"));
        Assert.Contains("/A << /S /URI /URI (https://x.y/a.md) >>", link, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownAnchorStaysAWarning()
    {
        using var warnings = new StringWriter();
        PdfDoc.Render("[x](#nowhere)", warnings: warnings);
        Assert.Contains("warning: link '#nowhere' matches no heading", warnings.ToString(), StringComparison.Ordinal);
    }
}
