namespace MdToPdf;

/// <summary>Command-line handling: arguments, file I/O and exit codes.</summary>
internal static class Cli
{
    private const string Usage = "Usage: mdtopdf [--paper a4|letter] <input.md> [output.pdf]";
    private const string PaperOption = "--paper";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is ["-h" or "--help"])
        {
            stderr.WriteLine(Usage);
            return 0;
        }

        var paper = Paper.A4;
        var files = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            string value;
            if (args[i] == PaperOption)
            {
                value = i + 1 < args.Length ? args[++i] : "";
            }
            else if (args[i].StartsWith(PaperOption + "=", StringComparison.Ordinal))
            {
                value = args[i][(PaperOption.Length + 1)..];
            }
            else if (args[i].Length > 1 && args[i][0] == '-')
            {
                files.Clear(); // unknown option (including -h among other args): show usage
                break;
            }
            else
            {
                files.Add(args[i]);
                continue;
            }

            if (Paper.Find(value) is not { } found)
            {
                stderr.WriteLine($"error: unknown paper size '{value}' (use a4 or letter)");
                return 1;
            }

            paper = found;
        }

        if (files.Count is not (1 or 2))
        {
            stderr.WriteLine(Usage);
            return 1;
        }

        var input = Path.GetFullPath(files[0]);
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

        var output = Path.GetFullPath(files.Count > 1 ? files[1] : Path.ChangeExtension(input, ".pdf"));
        var blocks = Markdown.Parse(markdown);
        var title = blocks.OfType<Heading>().FirstOrDefault(h => h.Level == 1) is { } h1
            ? Inlines.PlainText(h1.Content)
            : Path.GetFileNameWithoutExtension(input);

        // Render fully in memory first, so a failure never leaves a truncated PDF over a good one.
        var renderer = new Renderer(Path.GetDirectoryName(input)!, stderr, paper);
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
