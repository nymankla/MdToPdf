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
}
