using System.Globalization;
using System.Reflection;
using System.Text;

namespace MdToPdf;

/// <summary>Command-line handling: arguments, file I/O and exit codes.</summary>
internal static class Cli
{
    private const string Stdio = "-";
    private const string PaperOption = "--paper", MarginOption = "--margin", FontSizeOption = "--font-size";
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    private static readonly string PaperNames = string.Join('|', Paper.All.Select(p => p.Name));
    private static readonly string Usage = $"""
        Usage: mdtopdf [options] <input.md> [output.pdf]
               mdtopdf [options] <input.md> <input.md> ...   (or a pattern like *.md)

        Use - as the input to read stdin, and as the output to write stdout.

          --paper {PaperNames}   page size (default a4)
          --landscape               turn the page sideways
          --margin <pt>             page margin in points (default {PdfSyntax.N(RenderOptions.DefaultMargin)})
          --font-size <pt>          body text size in points (default {PdfSyntax.N(RenderOptions.DefaultFontSize)})
          --toc                     start with a table of contents
          --version                 show the version
        """;

    /// <summary>Runs the command. <paramref name="stdin"/> is read for the input <c>-</c> and <paramref name="stdoutBytes"/>
    /// written for the output <c>-</c>; they default to the process's own streams.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, Stream? stdin = null, Stream? stdoutBytes = null)
    {
        if (args is ["-h" or "--help"])
        {
            stderr.WriteLine(Usage);
            return 0;
        }

        if (args is ["--version"])
        {
            stdout.WriteLine($"mdtopdf {Assembly.GetExecutingAssembly().GetName().Version}");
            return 0;
        }

        if (!TryParseOptions(args, stderr, out var options, out var positional))
        {
            return 1;
        }

        if (positional.Count == 0 || positional.Any(string.IsNullOrWhiteSpace))
        {
            stderr.WriteLine(Usage);
            return 1;
        }

        // Two names are "input output", as long as the second doesn't read as another input.
        string? output = null;
        if (positional.Count == 2 && !IsMarkdownName(positional[1]))
        {
            output = positional[1];
            positional.RemoveAt(1);
        }

        var inputs = positional.SelectMany(ExpandPattern).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (inputs.Count > 1 && inputs.Any(i => i == Stdio || i.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            stderr.WriteLine(Usage);
            return 1;
        }

        // A bad file doesn't stop the others: each reports its own error.
        var failed = false;
        foreach (var input in inputs)
        {
            failed |= !Convert(input, output, options, stdout, stderr, stdin, stdoutBytes);
        }

        return failed ? 1 : 0;
    }

    private static bool IsMarkdownName(string name) =>
        Path.GetExtension(name).ToUpperInvariant() is ".MD" or ".MARKDOWN" or ".MDOWN" or ".MKD";

    /// <summary>cmd and PowerShell leave wildcards to the program. A pattern that matches nothing stays as it is, to fail as "file not found".</summary>
    private static IEnumerable<string> ExpandPattern(string name)
    {
        var file = Path.GetFileName(name);
        if (file.IndexOfAny(['*', '?']) < 0)
        {
            return [name];
        }

        try
        {
            var dir = Path.GetDirectoryName(name);
            var matches = Directory.GetFiles(string.IsNullOrEmpty(dir) ? "." : dir, file);
            Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
            return matches.Length > 0 ? matches : [name];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [name];
        }
    }

    private static bool TryParseOptions(string[] args, TextWriter stderr, out RenderOptions options, out List<string> positional)
    {
        var paper = Paper.A4;
        var landscape = false;
        var toc = false;
        var margin = RenderOptions.DefaultMargin;
        var fontSize = RenderOptions.DefaultFontSize;
        options = new RenderOptions(paper);
        positional = [];

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            var name = arg.StartsWith("--", StringComparison.Ordinal) && equals > 0 ? arg[..equals] : arg;
            string Value() => equals > 0 && name != arg ? arg[(equals + 1)..] : i + 1 < args.Length ? args[++i] : "";

            switch (name)
            {
                case PaperOption:
                    var value = Value();
                    if (Paper.Find(value) is not { } found)
                    {
                        stderr.WriteLine($"error: unknown paper size '{value}' (use {PaperNames.Replace("|", ", ", StringComparison.Ordinal)})");
                        return false;
                    }

                    paper = found;
                    break;
                case MarginOption:
                    if (!TryParseNumber(MarginOption, Value(), stderr, out margin))
                    {
                        return false;
                    }

                    break;
                case FontSizeOption:
                    if (!TryParseNumber(FontSizeOption, Value(), stderr, out fontSize))
                    {
                        return false;
                    }

                    break;
                case "--landscape":
                    landscape = true;
                    break;
                case "--toc":
                    toc = true;
                    break;
                default:
                    if (arg.Length > 1 && arg[0] == '-')
                    {
                        stderr.WriteLine(Usage); // unknown option, or -h among other args
                        return false;
                    }

                    positional.Add(arg);
                    break;
            }
        }

        var size = landscape ? paper.Landscape() : paper;
        if (Math.Min(size.Width, size.Height) - (2 * margin) < RenderOptions.MinContentSize)
        {
            stderr.WriteLine($"error: {MarginOption} {PdfSyntax.N(margin)} leaves no room on {size.Name} paper");
            return false;
        }

        if (fontSize is < RenderOptions.MinFontSize or > RenderOptions.MaxFontSize)
        {
            stderr.WriteLine($"error: {FontSizeOption} must be between {PdfSyntax.N(RenderOptions.MinFontSize)} and {PdfSyntax.N(RenderOptions.MaxFontSize)}");
            return false;
        }

        options = new RenderOptions(size, margin, fontSize, toc);
        return true;
    }

    private static bool TryParseNumber(string option, string value, TextWriter stderr, out double number)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && number >= 0)
        {
            return true;
        }

        stderr.WriteLine($"error: {option} needs a number of points, not '{value}'");
        return false;
    }

    /// <summary>Converts one input and reports any failure on <paramref name="stderr"/>.</summary>
    private static bool Convert(string input, string? explicitOutput, RenderOptions options, TextWriter stdout, TextWriter stderr, Stream? stdin, Stream? stdoutBytes)
    {
        var fromStdin = input == Stdio;
        var toStdout = explicitOutput == Stdio || (fromStdin && explicitOutput is null);

        string inputPath, outputPath;
        try
        {
            inputPath = fromStdin ? Path.GetFullPath("stdin") : Path.GetFullPath(input);
            outputPath = toStdout ? "" : Path.GetFullPath(explicitOutput ?? Path.ChangeExtension(inputPath, ".pdf"));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            stderr.WriteLine($"error: invalid path: {explicitOutput ?? input}");
            return false;
        }

        if (!fromStdin && !File.Exists(inputPath))
        {
            stderr.WriteLine($"error: file not found: {inputPath}");
            return false;
        }

        if (!fromStdin && !toStdout && string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine($"error: the output would overwrite the input: {inputPath}");
            return false;
        }

        byte[] bytes = [];
        if (!TryIo("read", fromStdin ? "stdin" : inputPath, stderr, () => bytes = fromStdin ? ReadAll(stdin ?? Console.OpenStandardInput()) : File.ReadAllBytes(inputPath)))
        {
            return false;
        }

        var markdown = Decode(bytes, out var isUtf8);
        if (!isUtf8)
        {
            stderr.WriteLine($"warning: {(fromStdin ? "stdin" : inputPath)} is not valid UTF-8; read as Windows-1252");
        }

        var document = Markdown.ParseDocument(markdown);
        var title = document.Title
            ?? (document.Blocks.OfType<Heading>().FirstOrDefault(h => h.Level == 1) is { } h1 ? Inlines.PlainText(h1.Content) : null)
            ?? (fromStdin ? "Document" : Path.GetFileNameWithoutExtension(inputPath));

        // Render fully in memory first, so a failure never leaves a truncated PDF over a good one.
        var renderer = new Renderer(Path.GetDirectoryName(inputPath)!, stderr, options);
        renderer.Render(document.Blocks);
        using var pdf = new MemoryStream();
        renderer.Save(pdf, title, document.Author, ReproducibleDate(Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH")));

        if (toStdout)
        {
            var target = stdoutBytes ?? Console.OpenStandardOutput();
            return TryIo("write", "stdout", stderr, () =>
            {
                pdf.WriteTo(target);
                target.Flush();
            });
        }

        if (!TryIo("write", outputPath, stderr, () => WriteAtomically(outputPath, pdf)))
        {
            return false;
        }

        stdout.WriteLine(outputPath);
        return true;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Decodes text by its byte order mark, else as UTF-8. Bytes that aren't valid UTF-8 are most likely an older Windows file.</summary>
    internal static string Decode(byte[] bytes, out bool isUtf8)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
            isUtf8 = true;
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            isUtf8 = false;
            return string.Create(bytes.Length, bytes, static (chars, b) =>
            {
                for (var i = 0; i < b.Length; i++)
                {
                    chars[i] = Fonts.FromWindows1252(b[i]);
                }
            });
        }
    }

    /// <summary>The fixed creation date asked for by the SOURCE_DATE_EPOCH convention, in seconds since 1970; null to use the current time.</summary>
    internal static DateTime? ReproducibleDate(string? epoch) =>
        long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    /// <summary>Writes to a temporary file and renames it over the target, so a full disk can't leave a truncated PDF behind.</summary>
    private static void WriteAtomically(string path, MemoryStream content)
    {
        // Renaming over a file another program has open fails with an unspecific error; opening it first gives the real one.
        if (File.Exists(path))
        {
            using var check = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }

        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                content.WriteTo(file);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static bool TryIo(string verb, string path, TextWriter stderr, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: cannot {verb} {path}: {Reason(e)}");
            return false;
        }
    }

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
