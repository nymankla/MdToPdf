using System.Globalization;
using System.Text.RegularExpressions;
using MdToPdf.Tests.Support;

namespace MdToPdf.Tests;

/// <summary>Rendering of bold code, footnotes, task lists, inline images, the contents and page options.</summary>
public sealed partial class RendererFeatureTests
{
    private static readonly byte[] TinyPng = ImageBuilder.Png(1, 1, 8, 0, [0, 0]);

    private static string Filler(int lines) => string.Join("\n\n", Enumerable.Range(1, lines).Select(i => $"Filler {i}"));

    // ---------------------------------------------------------------- text

    [Fact]
    public void BoldCodeUsesTheBoldMonoFont()
    {
        var run = Assert.Single(PdfDoc.Render("**`x`**").AllRuns(), r => r.Text == "x");
        Assert.Equal((int)FontId.MonoBold, run.Font);
    }

    [Fact]
    public void CodeInAHeadingStaysBold()
    {
        var run = Assert.Single(PdfDoc.Render("## Using `Foo` here").AllRuns(), r => r.Text == "Foo");
        Assert.Equal((int)FontId.MonoBold, run.Font);
    }

    [Fact]
    public void PlainCodeKeepsTheRegularMonoFont() =>
        Assert.Equal((int)FontId.Mono, Assert.Single(PdfDoc.Render("`x`").AllRuns(), r => r.Text == "x").Font);

    [Fact]
    public void InlineCodeKeepsItsSpacing() =>
        Assert.Single(PdfDoc.Render("x `a   b` y").AllRuns(), r => r.Text == "a   b");

    [Fact]
    public void RunsOfSpacesInTextCollapse() =>
        Assert.Single(PdfDoc.Render("a   b").AllRuns(), r => r.Text == "a b");

    [Fact]
    public void UnsupportedCharactersAreWarnedAboutOnce()
    {
        using var warnings = new StringWriter();
        PdfDoc.Render("中 中 😀\n\n| 中 |\n|---|\n| 中 |", warnings: warnings);
        var line = Assert.Single(warnings.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("2 character(s)", line, StringComparison.Ordinal);
        Assert.Contains("U+4E2D", line, StringComparison.Ordinal);
        Assert.Contains("U+1F600", line, StringComparison.Ordinal);
    }

    [Fact]
    public void HardBreakInATableCellIsNotAnUnsupportedCharacter()
    {
        using var warnings = new StringWriter();
        PdfDoc.Render("| a<br>b |\n|---|\n| c<br>d |", warnings: warnings);
        Assert.Empty(warnings.ToString());
    }

    [Fact]
    public void SwedishTextNeedsNoWarning()
    {
        using var warnings = new StringWriter();
        PdfDoc.Render("Smörgås – “räksmörgås” &auml; å", warnings: warnings);
        Assert.Empty(warnings.ToString());
    }

    // ---------------------------------------------------------------- saving

    [Fact]
    public void SavingTwiceIsRefused()
    {
        var renderer = new Renderer(Path.GetTempPath(), TextWriter.Null);
        renderer.Render(Markdown.Parse("x"));
        renderer.Save(Stream.Null, "t");
        Assert.Throws<InvalidOperationException>(() => renderer.Save(Stream.Null, "t"));
    }

    [Fact]
    public void InfoHasAuthorAndFixedDate()
    {
        var renderer = new Renderer(Path.GetTempPath(), TextWriter.Null);
        renderer.Render([]);
        using var ms = new MemoryStream();
        renderer.Save(ms, "t", "Åsa", new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var info = Assert.Single(PdfDoc.Parse(ms.ToArray()).ObjectsContaining("/Producer"));
        Assert.Contains($"/Author {PdfSyntax.TextString("Åsa")}", info, StringComparison.Ordinal);
        Assert.Contains("/CreationDate (D:20200102030405Z)", info, StringComparison.Ordinal);
    }

    [Fact]
    public void AFixedDateMakesTheOutputReproducible()
    {
        static byte[] Make()
        {
            var renderer = new Renderer(Path.GetTempPath(), TextWriter.Null);
            renderer.Render(Markdown.Parse("# Title\n\nText with a [link](http://x.y)."));
            using var ms = new MemoryStream();
            renderer.Save(ms, "t", null, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            return ms.ToArray();
        }

        Assert.Equal(Make(), Make());
    }

    // ---------------------------------------------------------------- images

    [Fact]
    public void InlineImageSitsInTheTextLine()
    {
        using var dir = new TempDir();
        dir.File("a.png", TinyPng);
        var doc = PdfDoc.Render("before ![alt](a.png) after", dir.Path);

        var content = doc.Content(0);
        Assert.Matches(@"q 0\.75 0 0 0\.75 [\d.]+ [\d.]+ cm /Im\d+ Do Q", content);
        Assert.DoesNotContain("(alt)", content, StringComparison.Ordinal);
        var before = Assert.Single(doc.AllRuns(), r => r.Text.StartsWith("before", StringComparison.Ordinal));
        var after = Assert.Single(doc.AllRuns(), r => r.Text.EndsWith("after", StringComparison.Ordinal));
        Assert.Equal(before.Y, after.Y);
        Assert.True(after.X > before.X);
    }

    [Fact]
    public void TallInlineImageMakesItsLineTaller()
    {
        using var dir = new TempDir();
        dir.File("a.png", ImageBuilder.Png(10, 200, 8, 0, new byte[200 * 11]));
        var doc = PdfDoc.Render("first\n\nx ![a](a.png) y\n\nlast", dir.Path);
        var first = Assert.Single(doc.AllRuns(), r => r.Text == "first");
        var last = Assert.Single(doc.AllRuns(), r => r.Text == "last");
        Assert.True(first.Y - last.Y > 150, $"{first.Y} - {last.Y}");
    }

    [Fact]
    public void WideInlineImageShrinksToTheColumn()
    {
        using var dir = new TempDir();
        dir.File("a.png", ImageBuilder.Png(2000, 1, 8, 0, new byte[2001]));
        var doc = PdfDoc.Render("![a](a.png) text", dir.Path);
        var width = double.Parse(ImageWidth().Match(doc.Content(0)).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(width <= 595.28 - 112 + 0.01, $"{width}");
    }

    [Fact]
    public void MissingInlineImageFallsBackToAltTextWithOneWarning()
    {
        using var warnings = new StringWriter();
        var doc = PdfDoc.Render("a ![the alt](nope.png) b ![the alt](nope.png)", warnings: warnings);
        Assert.Contains("the alt", doc.Text(), StringComparison.Ordinal);
        Assert.Single(warnings.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void LinkedImageIsClickable()
    {
        using var dir = new TempDir();
        dir.File("a.png", TinyPng);
        var doc = PdfDoc.Render("[![alt](a.png)](http://x.y)", dir.Path);
        Assert.Single(doc.ObjectsContaining("/S /URI"));
        Assert.Matches(@"/Im\d+ Do", doc.Content(0));
    }

    [Fact]
    public void ImageInTableCellIsEmbedded()
    {
        using var dir = new TempDir();
        dir.File("a.png", TinyPng);
        var doc = PdfDoc.Render("| h |\n|---|\n| ![a](a.png) |", dir.Path);
        Assert.Matches(@"/Im\d+ Do", doc.Content(0));
    }

    [Fact]
    public void SameFileUnderTwoNamesIsEmbeddedOnce()
    {
        using var dir = new TempDir();
        dir.File("a.png", TinyPng);
        using var warnings = new StringWriter();
        var doc = PdfDoc.Render("![x](a.png)\n\n![x](./a.png)\n\n![x](A.PNG)", dir.Path, warnings: warnings);
        Assert.Empty(warnings.ToString());
        Assert.Single(doc.ObjectsContaining("/Subtype /Image"));
        Assert.Equal(3, Regex.Count(doc.Content(0), @"/Im\d+ Do"));
    }

    [Theory]
    [InlineData("corrupt.png", "unsupported or corrupt")]
    [InlineData("missing.png", "file not found")]
    [InlineData("http://x.y/a.png", "remote images are not supported")]
    public void ImageProblemIsOneReadableWarning(string source, string reason)
    {
        using var dir = new TempDir();
        dir.File("corrupt.png", ImageBuilder.Png(4, 4, 8, 6, new byte[3]));
        using var warnings = new StringWriter();
        PdfDoc.Render($"![a]({source})", dir.Path, warnings: warnings);
        var line = Assert.Single(warnings.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains($"'{source}' not embedded: {reason}", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("img(1).png")]
    [InlineData("my pic.png")]
    public void ImagePathsWithParensAndSpacesAreFound(string file)
    {
        using var dir = new TempDir();
        dir.File(file, TinyPng);
        var source = file.Contains(' ', StringComparison.Ordinal) ? $"<{file}>" : file;
        Assert.Single(PdfDoc.Render($"![a]({source})", dir.Path).ObjectsContaining("/Subtype /Image"));
    }

    // ---------------------------------------------------------------- lists

    [Fact]
    public void ListMarkerStaysWithABigImage()
    {
        using var dir = new TempDir();
        dir.File("big.png", ImageBuilder.Png(400, 600, 8, 0, new byte[600 * 401]));
        var doc = PdfDoc.Render(Filler(20) + "\n\n- ![a](big.png)", dir.Path);

        var page = doc.Contents().ToList().FindIndex(c => Regex.IsMatch(c, @"/Im\d+ Do"));
        Assert.True(page > 0);
        Assert.Contains(@"(\225) Tj", doc.Content(page), StringComparison.Ordinal);
        Assert.DoesNotContain(@"(\225) Tj", doc.Content(page - 1), StringComparison.Ordinal);
    }

    [Fact]
    public void ListMarkerStaysWithATable()
    {
        var doc = PdfDoc.Render(Filler(32) + "\n\n- | a | b |\n  |---|---|\n  | 1 | 2 |\n");
        var marker = doc.Contents().ToList().FindIndex(c => c.Contains(@"(\225) Tj", StringComparison.Ordinal));
        var table = doc.Contents().ToList().FindIndex(c => c.Contains("(a) Tj", StringComparison.Ordinal));
        Assert.Equal(table, marker);
    }

    [Fact]
    public void TaskItemsDrawABoxInsteadOfAMarker()
    {
        var content = PdfDoc.Render("- [ ] open\n- [x] done\n- plain").Content(0);
        Assert.Equal(2, Regex.Count(content, @"re S"));
        Assert.Single(Regex.Matches(content, @"m [\d.]+ [\d.]+ l [\d.]+ [\d.]+ l S"));
        Assert.Single(Regex.Matches(content, @"\(\\225\) Tj"));
        Assert.DoesNotContain("[x]", content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- footnotes

    [Fact]
    public void FootnoteReferenceIsASmallRaisedLink()
    {
        var doc = PdfDoc.Render("word[^a]\n\n[^a]: The note.");
        var word = Assert.Single(doc.AllRuns(), r => r.Text == "word");
        var reference = Assert.Single(doc.AllRuns(), r => r.Text == "1");
        Assert.True(reference.Size < word.Size);
        Assert.True(reference.Y > word.Y);
        Assert.Contains("The note.", doc.Text(), StringComparison.Ordinal);
        Assert.Contains("/Dest", Assert.Single(doc.ObjectsContaining("/Subtype /Link")), StringComparison.Ordinal);
    }

    [Fact]
    public void FootnoteLinkJumpsToItsNote()
    {
        var doc = PdfDoc.Render($"word[^a]\n\n{Filler(80)}\n\n[^a]: The note.");
        var note = doc.Contents().ToList().FindIndex(c => c.Contains("(The note.) Tj", StringComparison.Ordinal));
        Assert.True(note > 0);
        var link = Assert.Single(doc.ObjectsContaining("/Subtype /Link"));
        Assert.Contains($"/Dest [{doc.PageIds[note]} 0 R", link, StringComparison.Ordinal);
    }

    [Fact]
    public void FootnotesAreNeitherBookmarksNorWarnings()
    {
        using var warnings = new StringWriter();
        var doc = PdfDoc.Render("x[^a]\n\n[^a]: note", warnings: warnings);
        Assert.Empty(doc.ObjectsContaining("/Outlines"));
        Assert.Empty(warnings.ToString());
    }

    // ---------------------------------------------------------------- contents

    private static readonly RenderOptions WithToc = new(Paper.A4, Toc: true);

    [Fact]
    public void ContentsComeFirstWithPageNumbersAndLinks()
    {
        var doc = PdfDoc.Render($"# One\n\n{Filler(80)}\n\n## Two", options: WithToc);
        var first = doc.Runs(0).Select(r => r.Text).ToList();
        Assert.Equal("Contents", first[0]);
        Assert.Contains("One", first);
        Assert.Contains("Two", first);

        var two = doc.Contents().ToList().FindLastIndex(c => c.Contains("(Two) Tj", StringComparison.Ordinal));
        Assert.True(two > 1);
        Assert.Contains((two + 1).ToString(CultureInfo.InvariantCulture), first);

        var links = doc.ObjectsContaining("/Subtype /Link").ToList();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.Contains($"/Dest [{doc.PageIds[two]} 0 R", StringComparison.Ordinal));
    }

    [Fact]
    public void BookmarksPointPastTheContents()
    {
        var doc = PdfDoc.Render("# One", options: WithToc);
        Assert.Equal(2, doc.PageCount);
        var bookmark = Assert.Single(doc.ObjectsContaining($"/Title {PdfSyntax.TextString("One")} "));
        Assert.Contains($"/Dest [{doc.PageIds[1]} 0 R", bookmark, StringComparison.Ordinal);
    }

    [Fact]
    public void LongContentsSpanPagesAndShiftThePageNumbers()
    {
        var headings = string.Join("\n\n", Enumerable.Range(1, 150).Select(i => $"## Heading {i}"));
        var doc = PdfDoc.Render(headings, options: WithToc);
        var tocPages = doc.Contents().Count(c => Regex.IsMatch(c, @"\. \. \. "));
        Assert.True(tocPages >= 2);

        var body = doc.Contents().ToList().FindIndex(c => c.Contains("(Heading 1) Tj", StringComparison.Ordinal) && !c.Contains(". . .", StringComparison.Ordinal));
        Assert.Equal(tocPages, body);
        Assert.Equal($"1 / {doc.PageCount}", doc.Runs(0)[^1].Text);
    }

    [Fact]
    public void NoHeadingsMeansNoContentsPage()
    {
        var doc = PdfDoc.Render("just text", options: WithToc);
        Assert.Equal(1, doc.PageCount);
        Assert.DoesNotContain("Contents", doc.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void ContentsLeaveFootnotesOut()
    {
        var doc = PdfDoc.Render("# One\n\nx[^a]\n\n[^a]: note", options: WithToc);
        Assert.DoesNotContain(doc.Runs(0), r => r.Text == "note");
    }

    // ---------------------------------------------------------------- page options

    [Fact]
    public void LandscapeSwapsThePageSize()
    {
        var doc = PdfDoc.Render("x", paper: Paper.A4.Landscape());
        Assert.Contains("/MediaBox [0 0 841.89 595.28]", doc.Objects[doc.PageIds[0]], StringComparison.Ordinal);
    }

    [Fact]
    public void MarginMovesTheText()
    {
        var doc = PdfDoc.Render("x", options: new RenderOptions(Paper.A4, Margin: 100));
        var run = Assert.Single(doc.AllRuns(), r => r.Text == "x");
        Assert.Equal(100, run.X);
        Assert.True(run.Y < 841.89 - 100);
    }

    [Fact]
    public void MarginNarrowsTheText()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 200));
        var wide = PdfDoc.Render(text);
        var narrow = PdfDoc.Render(text, options: new RenderOptions(Paper.A4, Margin: 150));
        Assert.True(narrow.AllRuns().Count > wide.AllRuns().Count);
    }

    [Fact]
    public void FontSizeScalesTextAndCode()
    {
        var doc = PdfDoc.Render("text\n\n# head\n\n```\ncode\n```", options: new RenderOptions(Paper.A4, FontSize: 21));
        Assert.Equal(21, Assert.Single(doc.AllRuns(), r => r.Text == "text").Size);
        Assert.Equal(42, Assert.Single(doc.AllRuns(), r => r.Text == "head").Size);
        Assert.Equal(18, Assert.Single(doc.AllRuns(), r => r.Text == "code").Size);
    }

    [GeneratedRegex(@"q ([\d.]+) 0 0 [\d.]+ [\d.]+ [\d.]+ cm /Im\d+ Do Q")]
    private static partial Regex ImageWidth();
}
