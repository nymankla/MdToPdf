using MdToPdf;

if (args is not [_] and not [_, _] || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: mdtopdf <input.md> [output.pdf]");
    return args is ["-h" or "--help"] ? 0 : 1;
}

var input = Path.GetFullPath(args[0]);
if (!File.Exists(input))
{
    Console.Error.WriteLine($"error: file not found: {input}");
    return 1;
}

var output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.ChangeExtension(input, ".pdf"));
var blocks = Markdown.Parse(File.ReadAllText(input));
var title = blocks.OfType<Heading>().FirstOrDefault(h => h.Level == 1) is { } h1
    ? Inlines.PlainText(h1.Content)
    : Path.GetFileNameWithoutExtension(input);

var renderer = new Renderer(Path.GetDirectoryName(input)!);
renderer.Render(blocks);
using (var stream = File.Create(output))
{
    renderer.Save(stream, title);
}

Console.WriteLine(output);
return 0;
