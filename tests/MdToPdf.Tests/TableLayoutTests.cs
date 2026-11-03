using MdToPdf.Tests.Support;

namespace MdToPdf.Tests;

/// <summary>A header taller than a page once made pagination loop forever, so these run with a time limit.</summary>
public sealed class TableLayoutTests
{
    private static async Task<PdfDoc> RenderInTime(string markdown) =>
        await Task.Run(() => PdfDoc.Render(markdown), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private static string Row(int cols, string cell) => "| " + string.Join(" | ", Enumerable.Repeat(cell, cols)) + " |\n";

    private static string Delimiter(int cols) => "|" + string.Concat(Enumerable.Repeat("---|", cols)) + "\n";

    private static string TallHeaderTable(int lines) =>
        $"| {string.Join("<br>", Enumerable.Range(1, lines).Select(i => $"h{i}"))} | b |\n|---|---|\n| first | row |\n| second | row |\n";

    [Theory]
    [InlineData(40)] // nearly filling a page
    [InlineData(60)] // taller than a page
    [InlineData(400)] // many pages tall
    public async Task TallHeaderIsSplitAndAllTextIsKept(int lines)
    {
        var doc = await RenderInTime(TallHeaderTable(lines));
        var text = doc.Text();
        Assert.Contains($"h{lines}", text, StringComparison.Ordinal);
        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.Contains("second", text, StringComparison.Ordinal);
        Assert.True(doc.PageCount <= (lines / 40) + 2, $"{doc.PageCount} pages");
    }

    [Fact]
    public async Task TallHeaderIsNotRepeated()
    {
        var doc = await RenderInTime(TallHeaderTable(60));
        Assert.Single(doc.AllRuns(), r => r.Text == "h1");
    }

    [Fact]
    public async Task TallHeaderIsShadedOnEveryPageItReaches()
    {
        var doc = await RenderInTime(TallHeaderTable(100));
        Assert.All(doc.Contents().Take(2), c => Assert.Contains("0.95 0.96 0.97 rg", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManyColumnsWithLongTitlesFinish()
    {
        var doc = await RenderInTime(Row(40, new string('a', 55)) + Delimiter(40) + Row(40, "x"));
        Assert.Contains("x", doc.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortHeaderStillRepeatsOnEveryPage()
    {
        var rows = string.Concat(Enumerable.Range(1, 200).Select(i => $"| {i} | x |\n"));
        var doc = await RenderInTime("| head | b |\n|---|---|\n" + rows);
        Assert.True(doc.PageCount > 1);
        Assert.Equal(doc.PageCount, doc.AllRuns().Count(r => r.Text == "head"));
    }

    [Fact]
    public async Task HeaderOnlyTableThatIsTooTallFinishes() =>
        Assert.Contains("h60", (await RenderInTime($"| {string.Join("<br>", Enumerable.Range(1, 60).Select(i => $"h{i}"))} |\n|---|\n")).Text(), StringComparison.Ordinal);
}
