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
    public void InlineImageKeepsAltInItalicForWhenItCantBeEmbedded() =>
        Assert.Equal(
            [new("see ", Style.None, null), new("alt", Style.Italic, null, "x.png")],
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

    // ---------------------------------------------------------------- entities and link forms

    [Theory]
    [InlineData("&auml;&aring;&ouml;&Auml;&Aring;&Ouml;", "äåöÄÅÖ")]
    [InlineData("&eacute;&szlig;&bull;", "éß•")]
    [InlineData("&ldquo;q&rdquo; &rarr;", "“q” →")]
    [InlineData("&frac12;&sup2;", "½²")]
    [InlineData("&#128;&#150;", "€–")]
    [InlineData("&#x1F600;", "😀")]
    public void MoreEntities(string md, string text) => Assert.Equal(text, One(md).Text);

    [Fact]
    public void NbspEntityIsANonBreakingSpace() => Assert.Equal("a\u00A0b", One("a&nbsp;b").Text);

    [Theory]
    [InlineData("[a](<my file.pdf>)", "my file.pdf")]
    [InlineData("[a](<my file.pdf> \"title\")", "my file.pdf")]
    [InlineData(@"[a](b\)c)", "b)c")]
    [InlineData("[a](x?a=1&amp;b=2)", "x?a=1&b=2")]
    [InlineData(@"[a](u\_v)", "u_v")]
    public void LinkDestinationForms(string md, string url) => Assert.Equal(url, One(md).Link);

    [Theory]
    [InlineData("before <!-- hidden --> after", "before  after")]
    [InlineData("<!-- x -->after", "after")]
    [InlineData("a <!-- b\nc --> d", "a  d")]
    [InlineData("a <!-- unclosed", "a <!-- unclosed")]
    [InlineData("`<!-- code -->`", "<!-- code -->")]
    public void InlineComments(string md, string text) => Assert.Equal(text, Plain(md));

    // ---------------------------------------------------------------- mixed emphasis

    [Fact]
    public void ShorterInnerRunClosesFirst() =>
        Assert.Equal(
            [new("a", Style.Bold, null), new("b", Style.Bold | Style.Italic, null)],
            Inlines.Parse("**a*b***"));

    [Fact]
    public void TripleOpenerSplitsBetweenTwoClosers() =>
        Assert.Equal(
            [new("a", Style.Bold | Style.Italic, null), new(" b", Style.Italic, null)],
            Inlines.Parse("***a** b*"));

    [Fact]
    public void TripleCloserSplitsBetweenTwoOpeners() =>
        Assert.Equal(
            [new("a ", Style.Italic, null), new("b", Style.Bold | Style.Italic, null)],
            Inlines.Parse("*a **b***"));

    [Fact]
    public void NearestOpenerWinsAndLeavesAMarkerOver() =>
        Assert.Equal([new("*", Style.None, null), new("a b", Style.Italic, null)], Inlines.Parse("**a *b**"));

    [Fact]
    public void ExtraOpeningMarkerStaysLiteral() =>
        Assert.Equal([new("*", Style.None, null), new("a", Style.Bold, null)], Inlines.Parse("***a**"));

    [Fact]
    public void StyleSpansAcrossLinkAndCode() =>
        Assert.Equal(
            [new("l", Style.Italic, "u"), new("c", Style.Italic | Style.Code, null)],
            Inlines.Parse("*[l](u)`c`*"));

    [Theory]
    [InlineData("foo*bar*", "bar", (int)Style.Italic)]
    [InlineData("foo**bar**baz", "bar", (int)Style.Bold)]
    [InlineData("(*a*)", "a", (int)Style.Italic)]
    public void IntrawordAsterisksEmphasize(string md, string text, int style) =>
        Assert.Contains(new Inline(text, (Style)style, null), Inlines.Parse(md));

    [Fact]
    public void IntrawordUnderscoresStayLiteral() => Assert.Equal("foo_bar_baz", One("foo_bar_baz").Text);

    [Fact]
    public void ThreeTildesAreLiteral() => Assert.Equal("~~~a~~~", One("~~~a~~~").Text);

    // ---------------------------------------------------------------- references

    private static InlineContext Context()
    {
        var context = new InlineContext();
        context.DefineLink("Ref One", "http://one");
        context.DefineFootnote("note", ["The note."]);
        return context;
    }

    [Theory]
    [InlineData("[text][ref one]", "text")]
    [InlineData("[ref one][]", "ref one")]
    [InlineData("[ref one]", "ref one")]
    [InlineData("[REF   ONE]", "REF ONE")]
    public void ReferenceLinks(string md, string text)
    {
        var link = Assert.Single(Inlines.Parse(md, Context()));
        Assert.Equal("http://one", link.Link);
        Assert.Equal(string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)), string.Join(' ', link.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Theory]
    [InlineData("[text][nope]")]
    [InlineData("[nope][]")]
    [InlineData("[nope]")]
    public void UnknownReferenceStaysLiteral(string md) => Assert.Equal(new Inline(md, Style.None, null), Assert.Single(Inlines.Parse(md, Context())));

    [Fact]
    public void ImageByReference() => Assert.Equal("http://one", Assert.Single(Inlines.Parse("![alt][ref one]", Context())).Image);

    [Fact]
    public void FootnoteReferenceIsASuperscriptLink()
    {
        var reference = Assert.Single(Inlines.Parse("[^note]", Context()));
        Assert.Equal(new Inline("1", Style.Super, "#^1"), reference);
    }

    [Fact]
    public void RepeatedFootnoteKeepsItsNumber()
    {
        var context = Context();
        context.DefineFootnote("other", ["Other."]);
        Assert.Equal(["1", " ", "2", " ", "1"], Inlines.Parse("[^note] [^other] [^note]", context).Select(i => i.Text));
        Assert.Equal(2, context.ReferencedNotes.Count);
    }

    [Fact]
    public void UnknownFootnoteStaysLiteral() => Assert.Equal("[^nope]", One("[^nope]").Text);

    // ---------------------------------------------------------------- image detection

    [Theory]
    [InlineData("![a](b.png)", "b.png", "a")]
    [InlineData("![a](img(1).png)", "img(1).png", "a")]
    [InlineData("![a](<my pic.png>)", "my pic.png", "a")]
    [InlineData("![a](b.png 'title')", "b.png", "a")]
    [InlineData("![a][ref one]", "http://one", "a")]
    public void WholeTextIsOneImage(string text, string source, string alt)
    {
        Assert.True(Inlines.TryImageOnly(text, Context(), out var s, out var a));
        Assert.Equal((source, alt), (s, a));
    }

    [Theory]
    [InlineData("![a](b.png) text")]
    [InlineData("text ![a](b.png)")]
    [InlineData("[a](b.png)")]
    [InlineData("![a]")]
    public void NotOneImage(string text) => Assert.False(Inlines.TryImageOnly(text, Context(), out _, out _));
}
