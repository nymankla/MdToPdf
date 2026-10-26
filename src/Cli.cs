namespace MdToPdf;

/// <summary>Command-line handling: arguments, file I/O and exit codes.</summary>
internal static class Cli
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is not [_] and not [_, _] || args[0] is "-h" or "--help")
        {
            stderr.WriteLine("Usage: mdtopdf <input.md> [output.pdf]");
            return args is ["-h" or "--help"] ? 0 : 1;
        }

        var input = Path.GetFullPath(args[0]);
        if (!File.Exists(input))
        {
            stderr.WriteLine($"error: file not found: {input}");
            return 1;
        }

        string markdown;
        try
        {
            markdown = File.ReadAllText(input);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: cannot read {input}: {Reason(e)}");
            return 1;
        }

        var output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.ChangeExtension(input, ".pdf"));
        var blocks = Markdown.Parse(markdown);
        var title = blocks.OfType<Heading>().FirstOrDefault(h => h.Level == 1) is { } h1
            ? Inlines.PlainText(h1.Content)
            : Path.GetFileNameWithoutExtension(input);

        // Render fully in memory first, so a failure never leaves a truncated PDF over a good one.
        var renderer = new Renderer(Path.GetDirectoryName(input)!, stderr);
        renderer.Render(blocks);
        using var pdf = new MemoryStream();
        renderer.Save(pdf, title);

        try
        {
            File.WriteAllBytes(output, pdf.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: cannot write {output}: {Reason(e)}");
            return 1;
        }

        stdout.WriteLine(output);
        return 0;
    }

    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    /// <summary>A readable cause. The AOT build strips exception messages down to resource keys, so common cases are spelled out.</summary>
    private static string Reason(Exception e) => e switch
    {
        DirectoryNotFoundException => "the folder does not exist",
        FileNotFoundException => "the file does not exist",
        UnauthorizedAccessException => "access denied",
        IOException { HResult: SharingViolation or LockViolation } => "the file is open in another program",
        _ => e.Message,
    };
}
