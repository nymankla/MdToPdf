using System.Globalization;
using System.Text.RegularExpressions;
using MdToPdf.Tests.Support;

namespace MdToPdf.Tests;

public sealed partial class RendererTests
{
    private const double Margin = 56, ContentWidth = 595.28 - (2 * Margin);
    private const string Ink = "0.12 0.14 0.16", Muted = "0.35 0.38 0.42", LinkColor = "0.04 0.41 0.85";
    private const string Shade = "0.95 0.96 0.97", Border = "0.82 0.84 0.87";

    /// <summary>Text runs without the page-number footers.</summary>
    private static List<TextRun> Body(PdfDoc doc) => [.. doc.AllRuns().Where(r => !(r.Size == 8 && Footer().IsMatch(r.Text)))];

    private static TextRun Run(PdfDoc doc, string text) => Assert.Single(Body(doc), r => r.Text == text);

    private static string Paragraphs(int count) => string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"Paragraph {i}"));

    private static List<string> Annotations(PdfDoc doc) => [.. doc.ObjectsContaining("/Subtype /Link")];

    private static string Outline(PdfDoc doc, string title) => Assert.Single(doc.ObjectsContaining($"/Title {PdfWriter.TextString(title)} "));

    private static string DestOf(string dict) => DestEntry().Match(dict).Groups[1].Value;

    // ---------------------------------------------------------------- document

    [Fact]
    public void EmptyDocumentHasOnePageWithFooter()
    {
        var doc = PdfDoc.Render("");
        Assert.Equal(1, doc.PageCount);
        Assert.Equal("1 / 1", Assert.Single(doc.AllRuns()).Text);
        Assert.Empty(doc.ObjectsContaining("/Outlines"));
    }

    [Fact]
    public void InfoHasTitleAndProducer()
    {
        var info = Assert.Single(PdfDoc.Render("x", title: "Résumé").ObjectsContaining("/Producer (MdToPdf)"));
        Assert.Contains($"/Title {PdfWriter.TextString("Résumé")} ", info, StringComparison.Ordinal);
        Assert.Matches(@"/CreationDate \(D:\d{14}Z\)", info);
    }

    [Fact]
    public void PagesAreA4AndShareResources()
    {
        var doc = PdfDoc.Render(Paragraphs(150));
        Assert.True(doc.PageCount > 1);
        var pages = doc.PageIds.Select(id => doc.Objects[id]).ToList();
        Assert.All(pages, p => Assert.Contains("/MediaBox [0 0 595.28 841.89]", p, StringComparison.Ordinal));
        Assert.Single(pages.Select(p => ResourcesEntry().Match(p).Value).Distinct());
    }

    [Fact]
    public void LetterPagesUseLetterSizeAndWidth()
    {
        var doc = PdfDoc.Render(string.Join(' ', Enumerable.Repeat("a", 400)), paper: Paper.Letter);
        Assert.Contains("/MediaBox [0 0 612 792]", doc.Objects[doc.PageIds[0]], StringComparison.Ordinal);

        // Lines fill the wider Letter column, past where A4's right margin would be.
        var right = Body(doc).Max(r => r.X + Fonts.Width(r.Text, (FontId)r.Font, r.Size));
        Assert.InRange(right, Margin + ContentWidth, 612 - Margin);

        var footer = Assert.Single(doc.Runs(0), r => r.Text == "1 / 1");
        Assert.Equal(612 / 2.0, footer.X + (Fonts.Width(footer.Text, FontId.Regular, 8) / 2), 1);
    }

    [Fact]
    public void LetterPagesBreakEarlierThanA4() =>
        Assert.True(PdfDoc.Render(Paragraphs(150), paper: Paper.Letter).PageCount > PdfDoc.Render(Paragraphs(150)).PageCount);

    [Theory]
    [InlineData("a4", 595.28)]
    [InlineData("LETTER", 612)]
    public void PaperIsFoundByNameIgnoringCase(string name, double width) => Assert.Equal(width, Paper.Find(name)?.Width);

    [Fact]
    public void UnknownPaperIsNull() => Assert.Null(Paper.Find("a5"));

    [Fact]
    public void XrefOffsetsPointAtObjects()
    {
        var doc = PdfDoc.Render("# Title\n\n[link](#title)\n\n| a |\n|---|\n| b |");
        var raw = doc.Raw;
        var xref = int.Parse(raw[(raw.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], CultureInfo.InvariantCulture);
        var entries = raw[xref..].Split('\n').Skip(3).TakeWhile(l => l.EndsWith(" n ", StringComparison.Ordinal)).ToList();
        Assert.Equal(doc.Offsets.Count, entries.Count);
        for (var k = 0; k < entries.Count; k++)
        {
            Assert.Equal(doc.Offsets[k + 1], int.Parse(entries[k][..10], CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void NumbersAreCultureInvariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var content = PdfDoc.Render("Some *text* and `code`").Content(0);
            Assert.Contains("10.5 Tf", content, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\d,\d", content);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData(2.0, "2")]
    [InlineData(1.5, "1.5")]
    [InlineData(1.234, "1.23")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(0.001, "0")]
    public void FormatsNumbersWithTwoDecimals(double value, string expected) => Assert.Equal(expected, Renderer.N(value));

    [Fact]
    public void RgbOperators()
    {
        var c = new Rgb(1, 0.5, 0.25);
        Assert.Equal("1 0.5 0.25 rg", c.Fill);
        Assert.Equal("1 0.5 0.25 RG", c.Stroke);
    }

    // ---------------------------------------------------------------- text

    [Fact]
    public void ParagraphUsesRegularFontAtBaseSize()
    {
        var run = Run(PdfDoc.Render("Hello world"), "Hello world");
        Assert.Equal((0, 10.5, Margin), (run.Font, run.Size, run.X));
    }

    [Theory]
    [InlineData("**x**", 1)]
    [InlineData("*x*", 2)]
    [InlineData("***x***", 3)]
    [InlineData("`x`", 4)]
    public void StylePicksFont(string md, int font) => Assert.Equal(font, Run(PdfDoc.Render(md), "x").Font);

    [Fact]
    public void InlineCodeIsSmallerAndShaded()
    {
        var doc = PdfDoc.Render("a `x` b");
        Assert.Equal(9.45, Run(doc, "x").Size);
        Assert.Contains($"{Shade} rg", doc.Content(0), StringComparison.Ordinal);
    }

    [Fact]
    public void StrikethroughDrawsLine() => Assert.Contains($"{Ink} RG 0.6 w", PdfDoc.Render("~~gone~~").Content(0), StringComparison.Ordinal);

    [Fact]
    public void WinAnsiTextSurvives() => Run(PdfDoc.Render("Café “quoted” €5 — ok"), "Café “quoted” €5 — ok");

    [Fact]
    public void LongParagraphWrapsWithinColumn()
    {
        var words = Enumerable.Range(1, 300).Select(i => $"word{i}").ToList();
        var runs = Body(PdfDoc.Render(string.Join(' ', words)));

        Assert.True(runs.Count > 10);
        Assert.All(runs, r => Assert.Equal(Margin, r.X));
        Assert.All(runs, r => Assert.True(Fonts.Width(r.Text, FontId.Regular, 10.5) <= ContentWidth + 0.01, r.Text));
        Assert.Equal(words, string.Join(' ', runs.Select(r => r.Text)).Split(' '));
    }

    [Fact]
    public void OverlongWordBreaksBetweenCharacters()
    {
        var word = new string('x', 500);
        var runs = Body(PdfDoc.Render(word));
        Assert.True(runs.Count > 1);
        Assert.Equal(word, string.Concat(runs.Select(r => r.Text)));
        Assert.All(runs, r => Assert.True(Fonts.Width(r.Text, FontId.Regular, 10.5) <= ContentWidth + 0.01));
    }

    [Fact]
    public void HardBreakStartsNewLine()
    {
        var doc = PdfDoc.Render("a  \nb");
        Assert.True(Run(doc, "a").Y > Run(doc, "b").Y);
        Assert.Equal(Run(doc, "a").X, Run(doc, "b").X);
    }

    [Fact]
    public void LongDocumentPaginatesWithNumberedFooters()
    {
        var doc = PdfDoc.Render(Paragraphs(150));
        Assert.True(doc.PageCount > 1);
        for (var p = 0; p < doc.PageCount; p++)
        {
            Assert.Contains(doc.Runs(p), r => r.Size == 8 && r.Text == $"{p + 1} / {doc.PageCount}");
        }

        Assert.Equal(Enumerable.Range(1, 150).Select(i => $"Paragraph {i}"), Body(doc).Select(r => r.Text));
    }

    // ---------------------------------------------------------------- headings, outline, links

    [Theory]
    [InlineData(1, 21.0, true)]
    [InlineData(2, 15.75, true)]
    [InlineData(3, 13.12, false)] // 13.125 rounds half-to-even
    [InlineData(6, 8.92, false)]
    public void HeadingSizeAndRule(int level, double size, bool underlined)
    {
        var doc = PdfDoc.Render($"{new string('#', level)} H");
        var run = Run(doc, "H");
        Assert.Equal((1, size), (run.Font, run.Size));
        Assert.Equal(underlined, doc.Content(0).Contains($"{Border} RG 0.75 w", StringComparison.Ordinal));
    }

    [Fact]
    public void LevelSixHeadingIsMuted() => Assert.Contains($"{Muted} rg", PdfDoc.Render("###### H").Content(0), StringComparison.Ordinal);

    [Fact]
    public void HeadingsBuildNestedOutline()
    {
        var doc = PdfDoc.Render("# A\n## B\n## C\n# D");
        Assert.Contains("/PageMode /UseOutlines", Assert.Single(doc.ObjectsContaining("/Type /Catalog")), StringComparison.Ordinal);
        Assert.Matches(@"^<< /Type /Outlines /First \d+ 0 R /Last \d+ 0 R /Count 4 >>$", Assert.Single(doc.ObjectsContaining("/Type /Outlines")));

        string a = Outline(doc, "A"), b = Outline(doc, "B"), c = Outline(doc, "C"), d = Outline(doc, "D");
        Assert.Contains("/Count 2", a, StringComparison.Ordinal);
        Assert.Contains("/Next", a, StringComparison.Ordinal);
        Assert.DoesNotContain("/Prev", b, StringComparison.Ordinal);
        Assert.Contains("/Next", b, StringComparison.Ordinal);
        Assert.Contains("/Prev", c, StringComparison.Ordinal);
        Assert.DoesNotContain("/Next", c, StringComparison.Ordinal);
        Assert.DoesNotContain("/Count", d, StringComparison.Ordinal);

        // B and C hang off A; A and D hang off the root.
        var aId = doc.Objects.Single(o => o.Value == a).Key;
        Assert.Contains($"/Parent {aId} 0 R", b, StringComparison.Ordinal);
        Assert.DoesNotContain($"/Parent {aId} 0 R", d, StringComparison.Ordinal);
    }

    [Fact]
    public void OutlineKeepsCharactersThePageFontsLack() => Outline(PdfDoc.Render("# Ω"), "Ω");

    [Fact]
    public void OutlineKeepsSymbolsThePageShowsAsStandIns()
    {
        var doc = PdfDoc.Render("# a → b");
        Outline(doc, "a → b");
        Run(doc, "a -> b");
    }

    [Fact]
    public void EntitiesGetTheSameStandInsAsLiterals() => Run(PdfDoc.Render("a → b &#8594; c"), "a -> b -> c");

    [Fact]
    public void CodeBlocksGetStandIns() => Run(PdfDoc.Render("```\nx → y\n```"), "x -> y");

    [Fact]
    public void InternalLinkJumpsToHeadingSlug()
    {
        var doc = PdfDoc.Render("# Hello, World!\n\n[go](#hello-world)");
        Assert.Equal(DestOf(Outline(doc, "Hello, World!")), DestOf(Assert.Single(Annotations(doc))));
    }

    [Fact]
    public void DuplicateHeadingsGetNumberedSlugs()
    {
        var doc = PdfDoc.Render("# Intro\n\ntext\n\n# Intro\n\n[x](#intro-1)");
        // Outline items are numbered in document order, so the second one is the second heading.
        var dests = doc.ObjectsContaining($"/Title {PdfWriter.TextString("Intro")} ").Select(DestOf).ToList();
        Assert.Equal(2, dests.Distinct().Count());
        Assert.Equal(dests[1], DestOf(Assert.Single(Annotations(doc))));
    }

    [Fact]
    public void LinkTargetsHeadingOnLaterPage()
    {
        var doc = PdfDoc.Render($"[jump](#the-end)\n\n{Paragraphs(150)}\n\n# The End");
        Assert.StartsWith($"[{doc.PageIds[^1]} 0 R /XYZ", DestOf(Assert.Single(Annotations(doc))), StringComparison.Ordinal);
    }

    [Fact]
    public void LongerHeadingDoesNotShiftDuplicateNumbering()
    {
        var doc = PdfDoc.Render("# Intro\n\n# Intro Part\n\n# Intro\n\n[x](#intro-1)");
        var intros = doc.ObjectsContaining($"/Title {PdfWriter.TextString("Intro")} ").Select(DestOf).ToList();
        Assert.Equal(intros[1], DestOf(Assert.Single(Annotations(doc))));
    }

    [Fact]
    public void DuplicateNumberingSkipsTakenSlugs()
    {
        // "A 1" takes "a-1", so the second "A" becomes "a-2" (as on GitHub).
        var doc = PdfDoc.Render("# A\n\n# A 1\n\n# A\n\n[x](#a-2)");
        var dests = doc.ObjectsContaining($"/Title {PdfWriter.TextString("A")} ").Select(DestOf).ToList();
        Assert.Equal(dests[1], DestOf(Assert.Single(Annotations(doc))));
    }

    [Fact]
    public void UnknownAnchorMakesNoAnnotation() => Assert.Empty(Annotations(PdfDoc.Render("[x](#nowhere)")));

    [Fact]
    public void UnknownAnchorWarnsOnce()
    {
        using var warnings = new StringWriter();
        PdfDoc.Render("[x](#nowhere) [y](#nowhere)", warnings: warnings);
        Assert.Equal("warning: link '#nowhere' matches no heading", warnings.ToString().Trim());
    }

    [Fact]
    public void NonAsciiUriIsPercentEncoded()
    {
        var doc = PdfDoc.Render("[x](https://sv.wikipedia.org/wiki/Göteborg)");
        Assert.Contains("/URI (https://sv.wikipedia.org/wiki/G%C3%B6teborg)", Assert.Single(Annotations(doc)), StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalLinkIsUriActionInLinkColor()
    {
        var doc = PdfDoc.Render("[site](https://x.y/(a))");
        Assert.Contains(@"/A << /S /URI /URI (https://x.y/\(a\)) >>", Assert.Single(Annotations(doc)), StringComparison.Ordinal);
        Assert.Contains($"{LinkColor} rg", doc.Content(0), StringComparison.Ordinal);
    }

    [Fact]
    public void LinkAnnotationCoversItsText()
    {
        var doc = PdfDoc.Render("[site](https://x.y)");
        var run = Run(doc, "site");
        var rect = RectEntry().Match(Assert.Single(Annotations(doc))).Groups.Cast<Group>().Skip(1)
            .Select(g => double.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(run.X, rect[0]);
        Assert.True(rect[1] < run.Y && run.Y < rect[3]);
        Assert.Equal(run.X + Fonts.Width("site", FontId.Regular, 10.5), rect[2], 1);
    }

    // ---------------------------------------------------------------- blocks

    [Fact]
    public void BulletMarkersAlternateWithDepth()
    {
        var runs = Body(PdfDoc.Render("- a\n  - b")).Select(r => r.Text).ToList();
        Assert.Equal(["•", "a", "–", "b"], runs);
    }

    [Fact]
    public void OrderedListNumbersFromStart()
    {
        var doc = PdfDoc.Render("3. x\n4. y");
        Assert.Equal(["3.", "x", "4.", "y"], Body(doc).Select(r => r.Text));
        Assert.Equal(Run(doc, "x").X, Run(doc, "y").X);
        Assert.True(Run(doc, "3.").X < Run(doc, "x").X);
    }

    [Fact]
    public void QuoteIsIndentedMutedWithBar()
    {
        var doc = PdfDoc.Render("> q");
        Assert.Equal(Margin + 14, Run(doc, "q").X);
        var content = doc.Content(0);
        Assert.Contains($"{Muted} rg", content, StringComparison.Ordinal);
        Assert.Matches($@"{Border} rg 56 [\d.]+ 3 [\d.]+ re f", content);
    }

    [Fact]
    public void QuoteBarContinuesAcrossPages()
    {
        var doc = PdfDoc.Render(string.Join("\n>\n", Enumerable.Range(1, 120).Select(i => $"> Quote {i}")));
        Assert.True(doc.PageCount > 1);
        Assert.All(doc.Contents(), c => Assert.Matches($@"{Border} rg 56 [\d.]+ 3 [\d.]+ re f", c));
    }

    [Fact]
    public void CodeBlockIsMonoPaddedAndShaded()
    {
        var doc = PdfDoc.Render("```\nlet x\n```");
        var run = Run(doc, "let x");
        Assert.Equal((4, 9.0, Margin + 8), (run.Font, run.Size, run.X));
        Assert.Contains($"{Shade} rg 56 ", doc.Content(0), StringComparison.Ordinal);
    }

    [Fact]
    public void CodeBlockBackgroundContinuesAcrossPages()
    {
        var doc = PdfDoc.Render("```\n" + string.Join('\n', Enumerable.Range(1, 150).Select(i => $"line {i}")) + "\n```");
        Assert.True(doc.PageCount > 1);
        Assert.All(doc.Contents(), c => Assert.Contains($"{Shade} rg 56 ", c, StringComparison.Ordinal));
        Assert.Equal(150, Body(doc).Count);
    }

    [Fact]
    public void LongCodeLineWrapsByCharacters()
    {
        var line = new string('x', 200);
        var runs = Body(PdfDoc.Render($"```\n{line}\n```"));
        Assert.Equal([86, 86, 28], runs.Select(r => r.Text.Length));
    }

    [Fact]
    public void EmptyCodeBlockStillDrawsBackground() =>
        Assert.Contains($"{Shade} rg", PdfDoc.Render("```\n```").Content(0), StringComparison.Ordinal);

    [Fact]
    public void RuleIsThickLine() => Assert.Contains($"{Border} RG 1.5 w", PdfDoc.Render("---").Content(0), StringComparison.Ordinal);

    // ---------------------------------------------------------------- tables

    [Fact]
    public void TableHeaderIsBoldAndShaded()
    {
        var doc = PdfDoc.Render("| Head |\n|---|\n| cell |");
        Assert.Equal(1, Run(doc, "Head").Font);
        Assert.Equal(0, Run(doc, "cell").Font);
        var content = doc.Content(0);
        Assert.Contains($"{Shade} rg", content, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(content, @"re S"));
    }

    [Fact]
    public void TableAlignment()
    {
        double X(string delim) => Run(PdfDoc.Render($"| Header |\n|{delim}|\n| x |"), "x").X;
        Assert.True(X("---") < X(":-:"));
        Assert.True(X(":-:") < X("--:"));
    }

    [Fact]
    public void TableHeaderRepeatsOnEveryPage()
    {
        var rows = string.Join('\n', Enumerable.Range(1, 120).Select(i => $"| {i} | row {i} |"));
        var doc = PdfDoc.Render($"| Num | Head |\n|---|---|\n{rows}");
        Assert.True(doc.PageCount > 1);
        for (var p = 0; p < doc.PageCount; p++)
        {
            Assert.Single(doc.Runs(p), r => r.Text == "Head");
        }
    }

    [Fact]
    public void RowTallerThanPageSplitsAcrossPages()
    {
        var words = Enumerable.Range(0, 1500).Select(i => $"w{i}").ToList();
        var doc = PdfDoc.Render($"| Key | Head |\n|---|---|\n| k | {string.Join(' ', words)} |");

        Assert.True(doc.PageCount > 2);
        Assert.All(Body(doc), r => Assert.True(r.Y >= Margin, $"'{r.Text}' drawn below the margin at y={r.Y}"));
        Assert.Equal(words, Body(doc).SelectMany(r => r.Text.Split(' ')).Where(w => w.StartsWith('w')));
        for (var p = 0; p < doc.PageCount; p++)
        {
            Assert.Single(doc.Runs(p), r => r.Text == "Head");
        }

        Assert.Single(Body(doc), r => r.Text == "k");
    }

    [Fact]
    public void RowThatFitsOnAFreshPageMovesWhole()
    {
        var tall = string.Join("<br>", Enumerable.Range(0, 20).Select(i => $"line{i}"));
        var doc = PdfDoc.Render($"{Paragraphs(40)}\n\n| H |\n|---|\n| {tall} |");
        Assert.Equal(2, doc.PageCount);
        Assert.All(Enumerable.Range(0, 20), i => Assert.Single(doc.Runs(1), r => r.Text == $"line{i}"));
    }

    [Fact]
    public void HugeTableCellLaysOutInLinearTime()
    {
        var cell = string.Join(' ', Enumerable.Range(0, 40_000).Select(i => $"w{i}"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        PdfDoc.Render($"| A | B |\n|---|---|\n| x | {cell} |");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}"); // quadratic took ~20 s
    }

    [Fact]
    public void WideTableShrinksToColumn()
    {
        var text = string.Join(' ', Enumerable.Repeat("words", 60));
        var doc = PdfDoc.Render($"| A | B |\n|---|---|\n| {text} | {text} |");
        Assert.All(Body(doc), r => Assert.True(r.X + Fonts.Width(r.Text, (FontId)r.Font, r.Size) <= Margin + ContentWidth, r.Text));
    }

    // ---------------------------------------------------------------- images

    private static byte[] GrayPng(int w, int h) => ImageBuilder.Png(w, h, 8, 0, ImageBuilder.Unfiltered(new byte[w * h], w, h, 1));

    [Fact]
    public void ImageIsDrawnAt96Dpi()
    {
        using var dir = new TempDir();
        dir.File("pic.png", GrayPng(4, 2));
        var doc = PdfDoc.Render("![pic](pic.png)", dir.Path);

        var image = Assert.Single(doc.Objects, o => o.Value.Contains("/Subtype /Image", StringComparison.Ordinal)).Key;
        Assert.Contains($"/XObject << /Im{image} {image} 0 R >>", Assert.Single(doc.ObjectsContaining("/Font <<")), StringComparison.Ordinal);
        Assert.Matches($@"q 3 0 0 1\.5 56 [\d.]+ cm /Im{image} Do Q", doc.Content(0));
    }

    [Fact]
    public void WideImageShrinksToColumn()
    {
        using var dir = new TempDir();
        dir.File("wide.png", GrayPng(2000, 10));
        Assert.Contains("q 483.28 0 0 2.42 56 ", PdfDoc.Render("![](wide.png)", dir.Path).Content(0), StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedImageIsEmbeddedOnce()
    {
        using var dir = new TempDir();
        dir.File("pic.png", GrayPng(2, 2));
        var doc = PdfDoc.Render("![](pic.png)\n\n![](pic.png)", dir.Path);
        Assert.Single(doc.ObjectsContaining("/Subtype /Image"));
        Assert.Equal(2, Regex.Count(doc.Content(0), " Do Q"));
    }

    [Fact]
    public void PercentEncodedImagePath()
    {
        using var dir = new TempDir();
        dir.File("my pic.png", GrayPng(2, 2));
        Assert.Single(PdfDoc.Render("![](my%20pic.png)", dir.Path).ObjectsContaining("/Subtype /Image"));
    }

    [Theory]
    [InlineData("![Alt text](missing.png)", "[image: Alt text]")]
    [InlineData("![](missing.png)", "[image: missing.png]")]
    [InlineData("![remote](https://example.com/a.png)", "[image: remote]")]
    [InlineData("![bad](bad.png)", "[image: bad]")]
    public void UnavailableImageShowsPlaceholder(string md, string placeholder)
    {
        using var dir = new TempDir();
        dir.File("bad.png", "not really a png");
        var doc = PdfDoc.Render(md, dir.Path);
        Assert.Equal(2, Run(doc, placeholder).Font);
        Assert.Empty(doc.ObjectsContaining("/Subtype /Image"));
    }

    // ---------------------------------------------------------------- samples

    [Theory]
    [InlineData("sample.md")]
    [InlineData("stress-table-code.md")]
    [InlineData("stress-quote-list.md")]
    public void SamplesRender(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", name);
        var doc = PdfDoc.Render(File.ReadAllText(path), Path.GetDirectoryName(path));
        Assert.True(doc.PageCount >= 1);
        Assert.NotEmpty(Body(doc));
    }

    [GeneratedRegex(@"^\d+ / \d+$")]
    private static partial Regex Footer();

    [GeneratedRegex(@"/Dest (\[[^\]]*\])")]
    private static partial Regex DestEntry();

    [GeneratedRegex(@"/Resources \d+ 0 R")]
    private static partial Regex ResourcesEntry();

    [GeneratedRegex(@"/Rect \[(\S+) (\S+) (\S+) (\S+)\]")]
    private static partial Regex RectEntry();
}
