namespace MdToPdf.Tests;

public sealed class MarkdownTests
{
    private static string Text(Block b) => b switch
    {
        Paragraph p => Inlines.PlainText(p.Content),
        Heading h => Inlines.PlainText(h.Content),
        _ => throw new ArgumentException($"no text in {b.GetType().Name}"),
    };

    private static T Single<T>(string md) where T : Block => Assert.IsType<T>(Assert.Single(Markdown.Parse(md)));

    // ---------------------------------------------------------------- headings

    [Theory]
    [InlineData("# One", 1, "One")]
    [InlineData("###### Six", 6, "Six")]
    [InlineData("## Closed ##", 2, "Closed")]
    [InlineData("   ### Indented", 3, "Indented")]
    public void AtxHeading(string md, int level, string text)
    {
        var h = Single<Heading>(md);
        Assert.Equal(level, h.Level);
        Assert.Equal(text, Text(h));
    }

    [Fact]
    public void AtxHeadingMayBeEmpty() => Assert.Empty(Single<Heading>("##").Content);

    [Theory]
    [InlineData("#hashtag")]
    [InlineData("####### seven")]
    public void NotAnAtxHeading(string md) => Assert.Equal(md, Text(Single<Paragraph>(md)));

    [Theory]
    [InlineData("Title\n=====", 1)]
    [InlineData("Title\n---", 2)]
    [InlineData("Title\n-", 2)]
    public void SetextHeading(string md, int level)
    {
        var h = Single<Heading>(md);
        Assert.Equal(level, h.Level);
        Assert.Equal("Title", Text(h));
    }

    [Fact]
    public void SetextHeadingSpansParagraphLines() => Assert.Equal("Two lines", Text(Single<Heading>("Two\nlines\n===")));

    // ---------------------------------------------------------------- paragraphs

    [Fact]
    public void BlankLineSeparatesParagraphs()
    {
        var blocks = Markdown.Parse("one\ntwo\n\n\nthree");
        Assert.Collection(
            blocks,
            b => Assert.Equal("one two", Text(b)),
            b => Assert.Equal("three", Text(b)));
    }

    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void LineEndingsAreNormalized(string md) => Assert.Equal("a b", Text(Single<Paragraph>(md)));

    [Fact]
    public void IndentedLineContinuesParagraph() => Assert.Equal("para more", Text(Single<Paragraph>("para\n    more")));

    [Fact]
    public void EmptyInputHasNoBlocks() => Assert.Empty(Markdown.Parse(""));

    // ---------------------------------------------------------------- sanitizing

    [Theory]
    [InlineData("\uFEFFHello", "Hello")]
    [InlineData("e\u0301", "\u00E9")]
    public void NormalizesInput(string md, string expected) => Assert.Equal(expected, Text(Single<Paragraph>(md)));

    [Fact]
    public void KeepsCharactersTheFontsLack() => Assert.Equal("a → b 😀", Text(Single<Paragraph>("a → b 😀")));

    // ---------------------------------------------------------------- code

    [Fact]
    public void FencedCodeKeepsContentVerbatim()
    {
        var c = Single<CodeBlock>("```cs\nvar x = *1*;\n\n  y\n```");
        Assert.Equal(["var x = *1*;", "", "  y"], c.Lines);
    }

    [Fact]
    public void TildeFenceClosedByLongerFence() => Assert.Equal(["a"], Single<CodeBlock>("~~~\na\n~~~~").Lines);

    [Fact]
    public void FenceOnlyClosesWithSameCharacter() =>
        Assert.Equal(["a", "~~~"], Single<CodeBlock>("```\na\n~~~\n```").Lines);

    [Fact]
    public void UnclosedFenceRunsToEnd() => Assert.Equal(["a", "b"], Single<CodeBlock>("```\na\nb").Lines);

    [Fact]
    public void IndentedFenceStripsItsIndentFromContent() =>
        Assert.Equal(["a", "  b"], Single<CodeBlock>("  ```\n  a\n    b\n  ```").Lines);

    [Fact]
    public void IndentedCodeDropsTrailingBlankLines()
    {
        var blocks = Markdown.Parse("    a\n\n    b\n\n\nPara");
        Assert.Collection(
            blocks,
            b => Assert.Equal(["a", "", "b"], Assert.IsType<CodeBlock>(b).Lines),
            b => Assert.Equal("Para", Text(b)));
    }

    [Fact]
    public void TabsExpandToFourColumns()
    {
        Assert.Equal(["code"], Single<CodeBlock>("\tcode").Lines);
        Assert.Equal(["a   b"], Single<CodeBlock>("```\na\tb\n```").Lines);
    }

    // ---------------------------------------------------------------- comments and rules

    [Fact]
    public void HtmlCommentIsSkipped() => Assert.Equal("Text", Text(Single<Paragraph>("<!-- a\nb -->\nText")));

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("- - -")]
    [InlineData("* * * *")]
    public void HorizontalRule(string md) => Single<Rule>(md);

    // ---------------------------------------------------------------- quotes

    [Fact]
    public void QuotesNest()
    {
        var q = Single<Quote>("> a\n> > b");
        Assert.Collection(
            q.Children,
            b => Assert.Equal("a", Text(b)),
            b => Assert.Equal("b", Text(Assert.Single(Assert.IsType<Quote>(b).Children))));
    }

    [Fact]
    public void QuoteTakesLazyContinuationLines() => Assert.Equal("a lazy", Text(Assert.Single(Single<Quote>("> a\nlazy").Children)));

    [Fact]
    public void QuoteMarkerWithoutSpace() => Assert.Equal("tight", Text(Assert.Single(Single<Quote>(">tight").Children)));

    [Fact]
    public void BlankLineEndsQuote()
    {
        var blocks = Markdown.Parse("> a\n\nb");
        Assert.IsType<Quote>(blocks[0]);
        Assert.Equal("b", Text(blocks[1]));
    }

    // ---------------------------------------------------------------- lists

    [Fact]
    public void BulletList()
    {
        var l = Single<ListBlock>("- a\n* b\n+ c");
        Assert.False(l.Ordered);
        Assert.Equal(["a", "b", "c"], l.Items.Select(i => Text(Assert.Single(i))));
    }

    [Theory]
    [InlineData("3. a\n4. b", 3)]
    [InlineData("1) a\n2) b", 1)]
    public void OrderedListKeepsStart(string md, int start)
    {
        var l = Single<ListBlock>(md);
        Assert.True(l.Ordered);
        Assert.Equal(start, l.Start);
        Assert.Equal(2, l.Items.Count);
    }

    [Fact]
    public void NestedList()
    {
        var item = Assert.Single(Single<ListBlock>("- a\n  - b\n  - c").Items);
        Assert.Equal("a", Text(item[0]));
        Assert.Equal(2, Assert.IsType<ListBlock>(item[1]).Items.Count);
    }

    [Fact]
    public void LooseListStaysOneList() => Assert.Equal(2, Single<ListBlock>("- a\n\n- b").Items.Count);

    [Fact]
    public void ItemWithSeveralParagraphs()
    {
        var item = Assert.Single(Single<ListBlock>("- a\n\n  b").Items);
        Assert.Equal(["a", "b"], item.Select(Text));
    }

    [Fact]
    public void ListItemTakesLazyContinuation() => Assert.Equal("a lazy", Text(Assert.Single(Single<ListBlock>("- a\nlazy").Items[0])));

    [Fact]
    public void ChangingListKindStartsNewList()
    {
        var blocks = Markdown.Parse("- a\n1. b");
        Assert.Collection(
            blocks,
            b => Assert.False(Assert.IsType<ListBlock>(b).Ordered),
            b => Assert.True(Assert.IsType<ListBlock>(b).Ordered));
    }

    [Fact]
    public void EmptyListItemCannotInterruptParagraph() => Assert.Equal("Para 1.", Text(Single<Paragraph>("Para\n1.")));

    [Fact]
    public void ListItemInterruptsParagraph()
    {
        var blocks = Markdown.Parse("Para\n- item");
        Assert.IsType<Paragraph>(blocks[0]);
        Assert.IsType<ListBlock>(blocks[1]);
    }

    [Fact]
    public void EmptyListItem() => Assert.Empty(Assert.Single(Single<ListBlock>("-").Items));

    // ---------------------------------------------------------------- tables

    [Fact]
    public void TableWithAlignmentAndPaddedRows()
    {
        var t = Single<Table>("| a | b | c |\n|:--|:-:|--:|\n| 1 | 2 | 3 |\n| 4 |");
        Assert.Equal(["a", "b", "c"], t.Header.Select(Inlines.PlainText));
        Assert.Equal([Align.Left, Align.Center, Align.Right], t.Aligns);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(["4", "", ""], t.Rows[1].Select(Inlines.PlainText));
    }

    [Fact]
    public void TableWithoutOuterPipes()
    {
        var t = Single<Table>("a | b\n--|--\n1 | 2");
        Assert.Equal(["1", "2"], t.Rows[0].Select(Inlines.PlainText));
    }

    [Fact]
    public void EscapedPipeStaysInCell()
    {
        var t = Single<Table>("| a \\| b | c |\n|---|---|\n| x | y |");
        Assert.Equal(["a | b", "c"], t.Header.Select(Inlines.PlainText));
    }

    [Fact]
    public void ColumnCountMismatchIsNotATable() => Assert.DoesNotContain(Markdown.Parse("| a | b |\n|---|\n| 1 | 2 |"), b => b is Table);

    [Fact]
    public void TableEndsAtLineWithoutPipe()
    {
        var blocks = Markdown.Parse("| a |\n|---|\n| 1 |\ntext");
        Assert.Single(Assert.IsType<Table>(blocks[0]).Rows);
        Assert.Equal("text", Text(blocks[1]));
    }

    [Fact]
    public void TableCellsParseInlines() =>
        Assert.Equal(Style.Bold, Assert.Single(Single<Table>("| **h** |\n|---|").Header[0]).Style);

    // ---------------------------------------------------------------- images

    [Theory]
    [InlineData("![Alt](img.png)", "img.png", "Alt")]
    [InlineData("![](a/b.jpg \"Title\")", "a/b.jpg", "")]
    [InlineData("![x](<pic.png>)", "pic.png", "x")]
    public void ImageOnlyParagraphIsImageBlock(string md, string source, string alt)
    {
        var img = Single<ImageBlock>(md);
        Assert.Equal(source, img.Source);
        Assert.Equal(alt, img.Alt);
    }

    [Fact]
    public void ImageInsideTextStaysInline() => Single<Paragraph>("See ![a](b.png) here");

    // ---------------------------------------------------------------- production settings

    [Fact]
    public void TestsRunUnderTheSameGlobalizationAsTheExe()
    {
        Assert.True(AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant));
        Assert.True(invariant);
    }

    [Theory]
    [InlineData("A\u030A", "Å")]
    [InlineData("a\u0308o\u0308", "äö")]
    [InlineData("n\u0303", "ñ")]
    [InlineData("s\u030C", "š")]
    public void ComposesAccents(string md, string expected) => Assert.Equal(expected, Text(Single<Paragraph>(md)));

    // ---------------------------------------------------------------- comments with content

    [Fact]
    public void TextAfterACommentIsKept() => Assert.Equal("kept", Text(Single<Paragraph>("<!-- c --> kept")));

    [Fact]
    public void TextAfterAMultiLineCommentIsKept() => Assert.Equal("kept", Text(Single<Paragraph>("<!-- a\nb --> kept")));

    [Fact]
    public void BlocksAfterACommentAreParsed() => Assert.Equal(2, Single<Heading>("<!-- c --> ## Title").Level);

    [Fact]
    public void UnclosedCommentRunsToTheEnd() => Assert.Empty(Markdown.Parse("<!-- open\nmore\ntext"));

    // ---------------------------------------------------------------- interruption

    [Theory]
    [InlineData("> q\n# Heading", typeof(Heading))]
    [InlineData("> q\n---", typeof(Rule))]
    [InlineData("> q\n- item", typeof(ListBlock))]
    [InlineData("> q\n1. item", typeof(ListBlock))]
    [InlineData("> q\n```\ncode\n```", typeof(CodeBlock))]
    public void BlockAfterAQuoteIsNotPulledIn(string md, Type second)
    {
        var blocks = Markdown.Parse(md);
        Assert.IsType<Quote>(blocks[0]);
        Assert.IsType(second, blocks[1]);
    }

    [Fact]
    public void QuoteDoesNotContinueAfterAnEmptyQuoteLine()
    {
        var blocks = Markdown.Parse("> q\n>\nlazy");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("lazy", Text(blocks[1]));
    }

    [Fact]
    public void LineStartingWithANumberStaysInTheParagraph() =>
        Assert.Equal("The year was 1986. A great year", Text(Single<Paragraph>("The year was\n1986. A great year")));

    [Fact]
    public void OrderedListFromOneInterruptsAParagraph()
    {
        var blocks = Markdown.Parse("Para\n1. item");
        Assert.IsType<Paragraph>(blocks[0]);
        Assert.True(Assert.IsType<ListBlock>(blocks[1]).Ordered);
    }

    [Fact]
    public void NumberedSiblingsAreStillOneList() => Assert.Equal(3, Single<ListBlock>("1. a\n2. b\n3. c").Items.Count);

    [Fact]
    public void NumberInsideAQuoteIsALazyLine() => Assert.Equal("a 2. b", Text(Assert.Single(Single<Quote>("> a\n2. b").Children)));

    // ---------------------------------------------------------------- images

    [Theory]
    [InlineData("![a](img(1).png)", "img(1).png")]
    [InlineData("![a](<my pic.png>)", "my pic.png")]
    public void ImagePathForms(string md, string source) => Assert.Equal(source, Single<ImageBlock>(md).Source);

    // ---------------------------------------------------------------- front matter

    [Fact]
    public void FrontMatterGivesTitleAndAuthor()
    {
        var doc = Markdown.ParseDocument("---\ntitle: My Title\nauthor: \"Åsa Ek\"\ndate: 2020\n---\n\nText");
        Assert.Equal("My Title", doc.Title);
        Assert.Equal("Åsa Ek", doc.Author);
        Assert.Equal("Text", Text(Assert.Single(doc.Blocks)));
    }

    [Fact]
    public void FrontMatterMayEndWithDots() => Assert.Equal("T", Markdown.ParseDocument("---\ntitle: T\n...\n").Title);

    [Fact]
    public void FrontMatterKeysIgnoreCase() => Assert.Equal("T", Markdown.ParseDocument("---\nTitle: T\n---\n").Title);

    [Fact]
    public void FrontMatterMayHoldListsAndComments() =>
        Assert.Equal("T", Markdown.ParseDocument("---\ntitle: T\n# comment\ntags:\n  - a\n- b\n---\n").Title);

    [Fact]
    public void FrontMatterWithoutTitleHasNone()
    {
        var doc = Markdown.ParseDocument("---\ndate: 2020\n---\nText");
        Assert.Null(doc.Title);
        Assert.Single(doc.Blocks);
    }

    [Theory]
    [InlineData("---\nJust a heading\n---\n")]
    [InlineData("---\n---\nText")]
    [InlineData("---\ntitle: x\nNo closing rule")]
    [InlineData("Text\n\n---\ntitle: x\n---\n")]
    public void OtherRulesAreNotFrontMatter(string md) => Assert.Null(Markdown.ParseDocument(md).Title);

    [Fact]
    public void RuleThenSetextHeadingStaysThat()
    {
        var blocks = Markdown.Parse("---\nJust a heading\n---\n");
        Assert.IsType<Rule>(blocks[0]);
        Assert.Equal("Just a heading", Text(Assert.IsType<Heading>(blocks[1])));
    }

    // ---------------------------------------------------------------- definitions

    private static Inline FirstInline(Block b) => Assert.IsType<Paragraph>(b).Content[0];

    [Theory]
    [InlineData("[a]: http://x")]
    [InlineData("[a]:   <http://x>")]
    [InlineData("[a]: http://x \"title\"")]
    [InlineData("[A]: http://x 'title'")]
    [InlineData("  [a]: http://x (title)")]
    public void LinkDefinitionsAreRemovedAndUsed(string definition)
    {
        var block = Assert.Single(Markdown.Parse($"[a]\n\n{definition}"));
        Assert.Equal("http://x", FirstInline(block).Link);
    }

    [Fact]
    public void DefinitionMayComeBeforeItsUse() => Assert.Equal("u", FirstInline(Assert.Single(Markdown.Parse("[a]: u\n\n[a]"))).Link);

    [Fact]
    public void ConsecutiveDefinitionsAreAllRecognized()
    {
        var content = Assert.IsType<Paragraph>(Assert.Single(Markdown.Parse("[a]: u\n[b]: v\n\n[a] [b]"))).Content;
        Assert.Equal(["u", null, "v"], content.Select(i => i.Link));
    }

    [Fact]
    public void FirstDefinitionWins() => Assert.Equal("u", FirstInline(Assert.Single(Markdown.Parse("[a]: u\n\n[a]: v\n\n[a]"))).Link);

    [Fact]
    public void DefinitionCantInterruptAParagraph() => Assert.Contains("[a]: u", Text(Single<Paragraph>("text\n[a]: u")), StringComparison.Ordinal);

    [Fact]
    public void DefinitionInFencedCodeIsCode()
    {
        Assert.Equal(["[a]: u"], Single<CodeBlock>("```\n[a]: u\n```").Lines);
    }

    [Fact]
    public void ReferencedImageIsAnImageBlock() => Assert.Equal("u.png", Assert.IsType<ImageBlock>(Assert.Single(Markdown.Parse("![a][r]\n\n[r]: u.png"))).Source);

    // ---------------------------------------------------------------- footnotes

    [Fact]
    public void FootnotesGoLastInReferenceOrder()
    {
        var blocks = Markdown.Parse("x[^b] y[^a]\n\n[^a]: First def\n[^b]: Second def\n\nz");
        var notes = Assert.IsType<FootnoteList>(blocks[^1]).Notes;
        Assert.Equal(["Second def", "First def"], notes.Select(n => Text(Assert.Single(n))));
        Assert.Equal("z", Text(blocks[1]));
    }

    [Fact]
    public void UnreferencedFootnoteIsDropped() => Assert.DoesNotContain(Markdown.Parse("x\n\n[^a]: note"), b => b is FootnoteList);

    [Fact]
    public void FootnoteTakesContinuationLinesAndParagraphs()
    {
        var note = Assert.Single(Assert.IsType<FootnoteList>(Markdown.Parse("x[^a]\n\n[^a]: one\nlazy\n\n    two\n\nafter")[^1]).Notes);
        Assert.Equal(["one lazy", "two"], note.Select(Text));
    }

    [Fact]
    public void FootnoteMayReferToAnotherFootnote()
    {
        var notes = Assert.IsType<FootnoteList>(Markdown.Parse("x[^a]\n\n[^a]: see[^b]\n[^b]: end")[^1]).Notes;
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void FootnoteDefinitionInFencedCodeIsCode() => Assert.Equal(["[^a]: x"], Single<CodeBlock>("```\n[^a]: x\n```").Lines);

    // ---------------------------------------------------------------- task lists

    [Fact]
    public void TaskItems()
    {
        var l = Single<ListBlock>("- [ ] open\n- [x] done\n- [X] loud\n- plain");
        Assert.Equal([false, true, true, null], l.Tasks!);
        Assert.Equal(["open", "done", "loud", "plain"], l.Items.Select(i => Text(Assert.Single(i))));
    }

    [Fact]
    public void OrdinaryListHasNoTasks() => Assert.Null(Single<ListBlock>("- a\n- b").Tasks);

    [Theory]
    [InlineData("- [a] x")]
    [InlineData("- [x]no space")]
    [InlineData("- [ ]")]
    public void NotATaskItem(string md) => Assert.Null(Single<ListBlock>(md).Tasks);
}
