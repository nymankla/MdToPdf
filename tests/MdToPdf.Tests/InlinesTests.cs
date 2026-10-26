namespace MdToPdf.Tests;

public sealed class InlinesTests
{
    private static Inline One(string md) => Assert.Single(Inlines.Parse(md));

    private static string Plain(string md) => Inlines.PlainText(Inlines.Parse(md));

    [Fact]
    public void PlainTextIsTrimmed() => Assert.Equal(new Inline("hello", Style.None, null), One("  hello  "));

    [Fact]
    public void EmptyInputHasNoRuns() => Assert.Empty(Inlines.Parse("   "));

    // ---------------------------------------------------------------- emphasis

    [Theory]
    [InlineData("*x*", (int)Style.Italic)]
    [InlineData("_x_", (int)Style.Italic)]
    [InlineData("**x**", (int)Style.Bold)]
    [InlineData("__x__", (int)Style.Bold)]
    [InlineData("***x***", (int)(Style.Bold | Style.Italic))]
    [InlineData("~~x~~", (int)Style.Strike)]
    [InlineData("`x`", (int)Style.Code)]
    public void SingleStyledRun(string md, int style) => Assert.Equal(new Inline("x", (Style)style, null), One(md));

    [Fact]
    public void EmphasisSplitsRuns() =>
        Assert.Equal(
            [new("a ", Style.None, null), new("b", Style.Italic, null), new(" c", Style.None, null)],
            Inlines.Parse("a *b* c"));

    [Fact]
    public void NestedEmphasisCombinesStyles() =>
        Assert.Equal(
            [new("a ", Style.Italic, null), new("b", Style.Bold | Style.Italic, null), new(" c", Style.Italic, null)],
            Inlines.Parse("*a **b** c*"));

    [Theory]
    [InlineData("snake_case_name")]
    [InlineData("* not emphasis*")]
    [InlineData("**unclosed")]
    [InlineData("~single~")]
    [InlineData("a * b * c")]
    public void LiteralDelimiters(string md) => Assert.Equal(new Inline(md, Style.None, null), One(md));

    [Fact]
    public void CloserInsideCodeSpanIsIgnored() =>
        Assert.Equal(
            [new("a ", Style.Italic, null), new("*", Style.Italic | Style.Code, null), new(" b", Style.Italic, null)],
            Inlines.Parse("*a `*` b*"));

    // ---------------------------------------------------------------- code spans

    [Theory]
    [InlineData("`*not bold*`", "*not bold*")]
    [InlineData("`` a`b ``", "a`b")]
    [InlineData("`  x  `", " x ")]
    [InlineData("`a\nb`", "a b")]
    public void CodeSpanContent(string md, string code) => Assert.Equal(new Inline(code, Style.Code, null), One(md));

    [Fact]
    public void UnclosedBackticksAreLiteral() => Assert.Equal("`open", Plain("`open"));

    // ---------------------------------------------------------------- links

    [Fact]
    public void InlineLinkDropsTitle() => Assert.Equal(new Inline("text", Style.None, "http://x.com"), One("[text](http://x.com \"title\")"));

    [Fact]
    public void LinkLabelKeepsStyling() => Assert.Equal(new Inline("b", Style.Bold, "u"), One("[**b**](u)"));

    [Fact]
    public void LinkDestinationWithBalancedParens() => Assert.Equal("u(1)", One("[a](u(1))").Link);

    [Fact]
    public void LinkDestinationInAngleBrackets() => Assert.Equal("u", One("[a](<u>)").Link);

    [Theory]
    [InlineData("[not a link]")]
    [InlineData("[a] (b)")]
    [InlineData("[a](unclosed")]
    public void NotALink(string md) => Assert.Equal(new Inline(md, Style.None, null), One(md));

    [Theory]
    [InlineData("<https://x.y/z>", "https://x.y/z", "https://x.y/z")]
    [InlineData("<me@x.com>", "me@x.com", "mailto:me@x.com")]
    [InlineData("<mailto:a@b.c>", "mailto:a@b.c", "mailto:a@b.c")]
    public void AutoLink(string md, string text, string link) => Assert.Equal(new Inline(text, Style.None, link), One(md));

    [Fact]
    public void BareUrlExcludesTrailingPunctuation() =>
        Assert.Equal(
            [new("see ", Style.None, null), new("https://example.com/a", Style.None, "https://example.com/a"), new(".", Style.None, null)],
            Inlines.Parse("see https://example.com/a."));

    [Fact]
    public void BareUrlMustStartAWord() => Assert.Null(One("xhttps://a.b").Link);

    [Fact]
    public void BareUrlInsideLinkLabelKeepsOuterLink() =>
        Assert.Equal(new Inline("https://a.b", Style.None, "https://c.d"), One("[https://a.b](https://c.d)"));

    [Fact]
    public void InlineImageShowsAltInItalic() =>
        Assert.Equal(
            [new("see ", Style.None, null), new("alt", Style.Italic, null)],
            Inlines.Parse("see ![alt](x.png)"));

    // ---------------------------------------------------------------- escapes, breaks, entities

    [Theory]
    [InlineData(@"\*lit\*", "*lit*")]
    [InlineData(@"\[x\]", "[x]")]
    [InlineData(@"\a", @"\a")]
    public void BackslashEscapes(string md, string text) => Assert.Equal(new Inline(text, Style.None, null), One(md));

    [Theory]
    [InlineData("a  \nb")]
    [InlineData("a\\\nb")]
    [InlineData("a<br>b")]
    [InlineData("a<BR />b")]
    public void HardBreak(string md) => Assert.Equal(["a", "\n", "b"], Inlines.Parse(md).Select(i => i.Text));

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a \n   b")]
    public void SoftBreakBecomesSpace(string md) => Assert.Equal("a b", One(md).Text);

    [Theory]
    [InlineData("&amp; &lt; &gt; &quot;", "& < > \"")]
    [InlineData("&copy; &euro; &mdash;", "© € —")]
    [InlineData("&#65;&#x42;&#X43;", "ABC")]
    [InlineData("&bogus;", "&bogus;")]
    [InlineData("&#0; &#xD800;", "? ?")]
    [InlineData("AT&T", "AT&T")]
    public void Entities(string md, string text) => Assert.Equal(text, One(md).Text);

    [Fact]
    public void PlainTextTurnsBreaksIntoSpaces() => Assert.Equal("a b c", Plain("a<br>b *c*"));
}
