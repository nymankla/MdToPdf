using System.Text;
using MdToPdf.Tests.Support;

namespace MdToPdf.Tests;

public sealed class CliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = Cli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string Title(string pdfPath)
    {
        var info = Assert.Single(PdfDoc.Parse(File.ReadAllBytes(pdfPath)).ObjectsContaining("/Producer (MdToPdf)"));
        var start = info.IndexOf("/Title <", StringComparison.Ordinal) + 7;
        return PdfDoc.DecodeTextString(info[start..(info.IndexOf('>', start) + 1)]);
    }

    [Theory]
    [InlineData]
    [InlineData("a.md", "b.pdf", "c")]
    public void WrongArgumentCountPrintsUsage(params string[] args)
    {
        var (code, _, err) = Run(args);
        Assert.Equal(1, code);
        Assert.StartsWith("Usage: mdtopdf", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void HelpPrintsUsageAndSucceeds(string flag)
    {
        var (code, _, err) = Run(flag);
        Assert.Equal(0, code);
        Assert.StartsWith("Usage: mdtopdf", err, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpWithOtherArgsFails() => Assert.Equal(1, Run("--help", "x.pdf").Code);

    [Fact]
    public void MissingInputFails()
    {
        using var dir = new TempDir();
        var (code, @out, err) = Run(dir.File("nope.md"));
        Assert.Equal(1, code);
        Assert.Empty(@out);
        Assert.Contains("file not found", err, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesPdfNextToInputByDefault()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var expected = Path.Combine(dir.Path, "doc.pdf");

        var (code, @out, err) = Run(input);
        Assert.Equal(0, code);
        Assert.Empty(err);
        Assert.Equal(expected, @out.Trim());
        Assert.StartsWith("%PDF-", File.ReadAllText(expected), StringComparison.Ordinal);
    }

    [Fact]
    public void WritesToExplicitOutput()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var output = dir.File("out.pdf");

        Assert.Equal(0, Run(input, output).Code);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(dir.Path, "doc.pdf")));
    }

    [Fact]
    public void TitleComesFromFirstLevelOneHeading()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "## Sub\n\n# My *Doc*\n\n# Second");
        Run(input);
        Assert.Equal("My Doc", Title(Path.ChangeExtension(input, ".pdf")));
    }

    [Fact]
    public void TitleFallsBackToFileName()
    {
        using var dir = new TempDir();
        var input = dir.File("notes.md", "## Only a subheading");
        Run(input);
        Assert.Equal("notes", Title(Path.ChangeExtension(input, ".pdf")));
    }

    private static string MediaBox(string pdfPath)
    {
        var doc = PdfDoc.Parse(File.ReadAllBytes(pdfPath));
        var page = doc.Objects[doc.PageIds[0]];
        var start = page.IndexOf("/MediaBox [", StringComparison.Ordinal);
        return page[start..(page.IndexOf(']', start) + 1)];
    }

    [Fact]
    public void DefaultPaperIsA4()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        Assert.Equal(0, Run(input).Code);
        Assert.Equal("/MediaBox [0 0 595.28 841.89]", MediaBox(Path.ChangeExtension(input, ".pdf")));
    }

    [Theory]
    [InlineData("--paper", "letter", "{in}")]
    [InlineData("{in}", "--paper", "Letter")]
    [InlineData("--paper=letter", "{in}", "{out}")]
    [InlineData("{in}", "{out}", "--paper", "LETTER")]
    public void PaperOptionSelectsLetter(params string[] args)
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var output = Path.Combine(dir.Path, "out.pdf");
        var resolved = args.Select(a => a.Replace("{in}", input, StringComparison.Ordinal).Replace("{out}", output, StringComparison.Ordinal)).ToArray();

        var (code, @out, err) = Run(resolved);
        Assert.Equal(0, code);
        Assert.Empty(err);
        Assert.Equal("/MediaBox [0 0 612 792]", MediaBox(@out.Trim()));
    }

    [Fact]
    public void PaperOptionCanBeSetBackToA4()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        Assert.Equal(0, Run("--paper", "letter", "--paper=a4", input).Code);
        Assert.Equal("/MediaBox [0 0 595.28 841.89]", MediaBox(Path.ChangeExtension(input, ".pdf")));
    }

    [Theory]
    [InlineData("--paper", "a9")]
    [InlineData("--paper=")]
    [InlineData("--paper")]
    public void UnknownPaperFails(params string[] option)
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var (code, @out, err) = Run([input, .. option]);
        Assert.Equal(1, code);
        Assert.Empty(@out);
        Assert.StartsWith("error: unknown paper size", err, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(input, ".pdf")));
    }

    [Fact]
    public void UnknownOptionPrintsUsage()
    {
        var (code, _, err) = Run("--bogus", "doc.md");
        Assert.Equal(1, code);
        Assert.StartsWith("Usage: mdtopdf [options]", err, StringComparison.Ordinal);
    }

    [Fact]
    public void LockedOutputFailsWithoutTouchingIt()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var output = dir.File("doc.pdf", "old");

        int code;
        string @out, err;
        using (new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (code, @out, err) = Run(input);
        }

        Assert.Equal(1, code);
        Assert.Empty(@out);
        Assert.Equal($"error: cannot write {output}: the file is open in another program", err.Trim());
        Assert.Equal("old", File.ReadAllText(output));
    }

    [Fact]
    public void MissingOutputFolderFails()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var output = Path.Combine(dir.Path, "no-such-dir", "out.pdf");

        var (code, @out, err) = Run(input, output);
        Assert.Equal(1, code);
        Assert.Empty(@out);
        Assert.Equal($"error: cannot write {output}: the folder does not exist", err.Trim());
    }

    [Fact]
    public void WarningsGoToStderr()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "![](missing.png)\n\n[x](#nowhere)");

        var (code, _, err) = Run(input);
        Assert.Equal(0, code);
        Assert.Contains("warning: image 'missing.png' not embedded", err, StringComparison.Ordinal);
        Assert.Contains("warning: link '#nowhere' matches no heading", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ImagesResolveRelativeToInput()
    {
        using var dir = new TempDir();
        dir.File("pic.png", ImageBuilder.Png(1, 1, 8, 0, [0, 0]));
        var input = dir.File("doc.md", "![](pic.png)");
        Run(input);
        Assert.Single(PdfDoc.Parse(File.ReadAllBytes(Path.ChangeExtension(input, ".pdf"))).ObjectsContaining("/Subtype /Image"));
    }

    // ---------------------------------------------------------------- protecting files

    [Fact]
    public void RefusesToOverwriteTheInput()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.txt", "# keep me");
        var (code, @out, err) = Run(input, input);
        Assert.Equal(1, code);
        Assert.Empty(@out);
        Assert.Contains("would overwrite the input", err, StringComparison.Ordinal);
        Assert.Equal("# keep me", File.ReadAllText(input));
    }

    [Fact]
    public void SameMarkdownFileTwiceIsConvertedOnceAndKept()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "# keep me");
        var (code, @out, _) = Run(input, input);
        Assert.Equal(0, code);
        Assert.Single(@out.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("# keep me", File.ReadAllText(input));
    }

    [Fact]
    public void RefusesToOverwriteTheInputUnderAnotherSpelling()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "# keep me");
        var other = Path.Combine(dir.Path, ".", "DOC.PDF");
        var pdf = dir.File("doc.pdf", "not really a pdf");
        var (code, _, err) = Run(pdf, other);
        Assert.Equal(1, code);
        Assert.Contains("would overwrite the input", err, StringComparison.Ordinal);
        Assert.Equal("not really a pdf", File.ReadAllText(pdf));
        Assert.Equal("# keep me", File.ReadAllText(input));
    }

    [Fact]
    public void RefusesAPdfAsItsOwnDefaultOutput()
    {
        using var dir = new TempDir();
        var pdf = dir.File("doc.pdf", "not really a pdf");
        Assert.Equal(1, Run(pdf).Code);
        Assert.Equal("not really a pdf", File.ReadAllText(pdf));
    }

    [Fact]
    public void LeavesNoTemporaryFilesBehind()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        Assert.Equal(0, Run(input).Code);
        Assert.Equal(["doc.md", "doc.pdf"], Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReplacesAnExistingOutput()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var output = dir.File("doc.pdf", "old");
        Assert.Equal(0, Run(input).Code);
        Assert.StartsWith("%PDF-", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputNamePrintsUsage(string name)
    {
        var (code, _, err) = Run(name);
        Assert.Equal(1, code);
        Assert.StartsWith("Usage: mdtopdf", err, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPathIsAnErrorNotACrash()
    {
        var (code, _, err) = Run("a\0b.md");
        Assert.Equal(1, code);
        Assert.StartsWith("error: invalid path", err, StringComparison.Ordinal);
    }

    [Fact]
    public void HugeImageIsAWarningNotACrash()
    {
        using var dir = new TempDir();
        dir.File("huge.png", ImageBuilder.Png(30000, 30000, 8, 6, new byte[16]));
        var input = dir.File("doc.md", "![a](huge.png)");
        var (code, _, err) = Run(input);
        Assert.Equal(0, code);
        Assert.Contains("'huge.png' not embedded: unsupported or corrupt", err, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- encodings

    [Fact]
    public void Windows1252FileIsDecodedWithAWarning()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", [.. "Sm"u8, 0xF6, .. "rg"u8, 0xE5, (byte)'s', (byte)' ', 0x80]);
        var (code, @out, err) = Run(input);
        Assert.Equal(0, code);
        Assert.Contains("not valid UTF-8", err, StringComparison.Ordinal);
        Assert.Equal("Smörgås €", Assert.Single(PdfDoc.Parse(File.ReadAllBytes(@out.Trim())).AllRuns(), r => r.Size != 8).Text);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public void UnicodeFilesNeedNoWarning(string encoding)
    {
        var bytes = encoding switch
        {
            "utf-8" => Encoding.UTF8.GetBytes("Smörgås"),
            "utf-8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Smörgås")],
            "utf-16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Smörgås")],
            _ => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("Smörgås")],
        };

        using var dir = new TempDir();
        var (code, @out, err) = Run(dir.File("doc.md", bytes));
        Assert.Equal(0, code);
        Assert.Empty(err);
        Assert.Equal("Smörgås", Assert.Single(PdfDoc.Parse(File.ReadAllBytes(@out.Trim())).AllRuns(), r => r.Size != 8).Text);
    }

    // ---------------------------------------------------------------- several files

    [Fact]
    public void SeveralInputsEachGetAPdf()
    {
        using var dir = new TempDir();
        var a = dir.File("a.md", "A");
        var b = dir.File("b.md", "B");
        var (code, @out, err) = Run(a, b);
        Assert.Equal(0, code);
        Assert.Empty(err);
        Assert.True(File.Exists(Path.ChangeExtension(a, ".pdf")));
        Assert.True(File.Exists(Path.ChangeExtension(b, ".pdf")));
        Assert.Equal(2, @out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ThreeInputsWork()
    {
        using var dir = new TempDir();
        Assert.Equal(0, Run(dir.File("a.md", "A"), dir.File("b.md", "B"), dir.File("c.md", "C")).Code);
        Assert.Equal(3, Directory.GetFiles(dir.Path, "*.pdf").Length);
    }

    [Fact]
    public void PatternExpandsToMatchingFiles()
    {
        using var dir = new TempDir();
        dir.File("a.md", "A");
        dir.File("b.md", "B");
        dir.File("c.txt", "C");
        var (code, _, _) = Run(Path.Combine(dir.Path, "*.md"));
        Assert.Equal(0, code);
        Assert.Equal(["a.pdf", "b.pdf"], Directory.GetFiles(dir.Path, "*.pdf").Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PatternMatchingNothingFails()
    {
        using var dir = new TempDir();
        var (code, _, err) = Run(Path.Combine(dir.Path, "*.md"));
        Assert.Equal(1, code);
        Assert.Contains("file not found", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadFileDoesNotStopTheOthers()
    {
        using var dir = new TempDir();
        var good = dir.File("good.md", "G");
        var (code, _, err) = Run(dir.File("missing.md"), good);
        Assert.Equal(1, code);
        Assert.Contains("file not found", err, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.ChangeExtension(good, ".pdf")));
    }

    [Fact]
    public void SecondNameWithAnOtherExtensionIsTheOutput()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var output = Path.Combine(dir.Path, "out.bin");
        Assert.Equal(0, Run(input, output).Code);
        Assert.True(File.Exists(output));
    }

    // ---------------------------------------------------------------- stdin and stdout

    private static (int Code, string Out, string Err, byte[] Pdf) RunPiped(string markdown, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        using var pdf = new MemoryStream();
        var code = Cli.Run(args, stdout, stderr, input, pdf);
        return (code, stdout.ToString(), stderr.ToString(), pdf.ToArray());
    }

    [Fact]
    public void StdinGoesToStdoutByDefault()
    {
        var (code, @out, err, pdf) = RunPiped("# Hej", "-");
        Assert.Equal(0, code);
        Assert.Empty(@out);
        Assert.Empty(err);
        Assert.Equal("Hej", Assert.Single(PdfDoc.Parse(pdf).AllRuns(), r => r.Size != 8).Text);
    }

    [Fact]
    public void StdinCanGoToAFile()
    {
        using var dir = new TempDir();
        var output = dir.File("out.pdf");
        var (code, @out, _, pdf) = RunPiped("Hello", "-", output);
        Assert.Equal(0, code);
        Assert.Equal(output, @out.Trim());
        Assert.Empty(pdf);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void FileCanGoToStdout()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var (code, @out, _, pdf) = RunPiped("", input, "-");
        Assert.Equal(0, code);
        Assert.Empty(@out);
        Assert.Equal("%PDF-"u8.ToArray(), pdf[..5]);
        Assert.False(File.Exists(Path.ChangeExtension(input, ".pdf")));
    }

    [Fact]
    public void StdinTitleHasAFallback()
    {
        var info = Assert.Single(PdfDoc.Parse(RunPiped("text", "-").Pdf).ObjectsContaining("/Producer"));
        Assert.Contains($"/Title {PdfSyntax.TextString("Document")}", info, StringComparison.Ordinal);
    }

    [Fact]
    public void StdinCantBeOneOfSeveralInputs()
    {
        var (code, _, err, _) = RunPiped("x", "-", "a.md", "b.md");
        Assert.Equal(1, code);
        Assert.StartsWith("Usage", err, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- options

    [Fact]
    public void VersionIsPrinted()
    {
        var (code, @out, _) = Run("--version");
        Assert.Equal(0, code);
        Assert.Matches(@"^mdtopdf \d+\.\d+\.\d+\.\d+\s*$", @out);
    }

    [Theory]
    [InlineData("a3", "841.89 1190.55")]
    [InlineData("a5", "419.53 595.28")]
    [InlineData("legal", "612 1008")]
    public void MorePaperSizes(string name, string size)
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        Assert.Equal(0, Run("--paper", name, input).Code);
        Assert.Equal($"/MediaBox [0 0 {size}]", MediaBox(Path.ChangeExtension(input, ".pdf")));
    }

    [Fact]
    public void LandscapeTurnsThePage()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        Assert.Equal(0, Run(input, "--landscape").Code);
        Assert.Equal("/MediaBox [0 0 841.89 595.28]", MediaBox(Path.ChangeExtension(input, ".pdf")));
    }

    [Theory]
    [InlineData("--margin", "100")]
    [InlineData("--margin=100")]
    public void MarginOption(params string[] option)
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        Assert.Equal(0, Run([.. option, input]).Code);
        Assert.Equal(100, PdfDoc.Parse(File.ReadAllBytes(Path.ChangeExtension(input, ".pdf"))).AllRuns()[0].X);
    }

    [Theory]
    [InlineData("--font-size", "14.5")]
    [InlineData("--font-size=14.5")]
    public void FontSizeOption(params string[] option)
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        Assert.Equal(0, Run([.. option, input]).Code);
        Assert.Equal(14.5, PdfDoc.Parse(File.ReadAllBytes(Path.ChangeExtension(input, ".pdf"))).AllRuns()[0].Size);
    }

    [Theory]
    [InlineData("--margin", "wide")]
    [InlineData("--margin", "-5")]
    [InlineData("--margin", "NaN")]
    [InlineData("--margin", "300")] // nothing left of the page
    [InlineData("--margin")]
    [InlineData("--font-size", "big")]
    [InlineData("--font-size", "1")]
    [InlineData("--font-size", "500")]
    public void BadNumbersFailWithAnError(params string[] option)
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "Hello");
        var (code, _, err) = Run([input, .. option]);
        Assert.Equal(1, code);
        Assert.StartsWith("error: --", err, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(input, ".pdf")));
    }

    [Fact]
    public void TocOptionAddsAContentsPage()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "# One");
        Assert.Equal(0, Run("--toc", input).Code);
        Assert.Equal(2, PdfDoc.Parse(File.ReadAllBytes(Path.ChangeExtension(input, ".pdf"))).PageCount);
    }

    [Fact]
    public void UsageListsEveryOptionAndPaper()
    {
        var (_, _, err) = Run("--help");
        foreach (var word in new[] { "--paper", "--landscape", "--margin", "--font-size", "--toc", "--version", "a3", "legal" })
        {
            Assert.Contains(word, err, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- metadata

    [Fact]
    public void FrontMatterTitleBeatsTheHeading()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.md", "---\ntitle: From YAML\nauthor: Åsa\n---\n\n# From heading");
        Assert.Equal(0, Run(input).Code);
        var output = Path.ChangeExtension(input, ".pdf");
        Assert.Equal("From YAML", Title(output));
        var info = Assert.Single(PdfDoc.Parse(File.ReadAllBytes(output)).ObjectsContaining("/Producer"));
        Assert.Contains($"/Author {PdfSyntax.TextString("Åsa")}", info, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", 1970)]
    [InlineData("1577836800", 2020)]
    public void ValidEpochIsAFixedDate(string epoch, int year) => Assert.Equal(year, Cli.ReproducibleDate(epoch)?.Year);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-1")]
    [InlineData("99999999999999999")]
    public void OtherEpochsMeanNow(string? epoch) => Assert.Null(Cli.ReproducibleDate(epoch));
}
