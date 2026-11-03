using MdToPdf.Tests.Support;

namespace MdToPdf.Tests;

/// <summary>Whatever the input, the converter must finish and produce a parsable PDF.</summary>
public sealed class RobustnessTests
{
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(60);

    private static readonly string[] Fragments =
    [
        "# ", "## ", "###### ", "> ", "- ", "* ", "1. ", "2) ", "- [ ] ", "- [x] ", "    ", "\t", "\n", "\n\n", "---\n", "***\n",
        "```\n", "~~~\n", "| a | b |\n", "|---|---|\n", "| 1 |\n", "<!--", "-->", "<br>", "*", "**", "***", "_", "__", "~~", "`", "``", "[", "]", "(", ")",
        "[a](b)", "[a][r]", "[r]: http://x.y\n", "[^1]", "[^1]: note\n", "![a](missing.png)", "![a][r]", "<http://x.y>", "http://x.y", "&amp;", "&auml;", "&#x41;", "&bad;",
        "\\", "\\*", "word ", "åäö ", "→", "😀", "́", "\uD83D", "​", "|", ":", "title: x\n",
    ];

    private static async Task ConvertInTime(string markdown)
    {
        var doc = await Task.Run(() => PdfDoc.Render(markdown), TestContext.Current.CancellationToken)
            .WaitAsync(TimeLimit, TestContext.Current.CancellationToken);
        Assert.True(doc.PageCount >= 1);
    }

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 200)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task RandomMarkdownConverts(int seed)
    {
        var random = new Random(seed); // a fixed seed, so a failure can be reproduced
        var parts = Enumerable.Range(0, random.Next(1, 200)).Select(_ => Fragments[random.Next(Fragments.Length)]);
        await ConvertInTime(string.Concat(parts));
    }

    [Theory]
    [InlineData(">", 100_000)] // nested quotes overflowed the stack
    [InlineData("> ", 5_000)]
    [InlineData("[", 200_000)] // each bracket used to rescan the rest of the text
    [InlineData("[a](", 50_000)]
    [InlineData("]", 200_000)]
    [InlineData("*a ", 60_000)] // each marker used to rescan the rest of the text
    [InlineData("_a ", 60_000)]
    [InlineData("a* ", 60_000)]
    [InlineData("**a *b ", 20_000)]
    [InlineData("~~a ", 40_000)]
    [InlineData("`a ", 60_000)]
    [InlineData("![", 100_000)]
    [InlineData("<!--", 50_000)]
    [InlineData("&", 100_000)]
    public async Task RepeatedMarkersConvertInTime(string fragment, int count) =>
        await ConvertInTime(string.Concat(Enumerable.Repeat(fragment, count)));

    [Fact]
    public async Task DeeplyNestedLinksConvertInTime() =>
        await ConvertInTime(string.Concat(Enumerable.Repeat("[", 5_000)) + "x" + string.Concat(Enumerable.Repeat("](u)", 5_000)));

    [Fact]
    public async Task DeeplyNestedEmphasisConvertsInTime() =>
        await ConvertInTime(string.Concat(Enumerable.Repeat("*a ", 20_000)) + string.Concat(Enumerable.Repeat("a* ", 20_000)));

    [Fact]
    public async Task DeeplyNestedListsConvertInTime() =>
        await ConvertInTime(string.Join('\n', Enumerable.Range(0, 3_000).Select(i => new string(' ', i * 2) + "- x")));

    [Fact]
    public void NestingBeyondTheLimitBecomesText()
    {
        var blocks = Markdown.Parse(string.Concat(Enumerable.Repeat("> ", Limits.MaxNesting + 5)) + "deep");
        var depth = 0;
        while (blocks is [Quote q])
        {
            blocks = q.Children;
            depth++;
        }

        Assert.Equal(Limits.MaxNesting, depth);
        var text = Inlines.PlainText(Assert.IsType<Paragraph>(Assert.Single(blocks)).Content);
        Assert.EndsWith("deep", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowColumnsStopIndentingInsteadOfVanishing()
    {
        var doc = PdfDoc.Render(string.Concat(Enumerable.Repeat("> ", Limits.MaxNesting)) + "readable");
        var run = Assert.Single(doc.AllRuns(), r => r.Text == "readable");
        Assert.True(run.X + 50 < 595.28, $"x = {run.X}");
    }
}
