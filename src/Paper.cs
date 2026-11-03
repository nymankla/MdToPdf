namespace MdToPdf;

/// <summary>A page size in PDF points (1/72 inch).</summary>
internal sealed record Paper(string Name, double Width, double Height)
{
    public static readonly Paper A3 = new("a3", 841.89, 1190.55);
    public static readonly Paper A4 = new("a4", 595.28, 841.89);
    public static readonly Paper A5 = new("a5", 419.53, 595.28);
    public static readonly Paper Letter = new("letter", 612, 792);
    public static readonly Paper Legal = new("legal", 612, 1008);

    public static readonly Paper[] All = [A3, A4, A5, Letter, Legal];

    /// <summary>Looks a paper size up by name, ignoring case; null when unknown.</summary>
    public static Paper? Find(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Paper Landscape() => this with { Width = Height, Height = Width };
}

/// <summary>Layout settings for a document.</summary>
internal sealed record RenderOptions(Paper Paper, double Margin = RenderOptions.DefaultMargin, double FontSize = RenderOptions.DefaultFontSize, bool Toc = false)
{
    public const double DefaultMargin = 56, DefaultFontSize = 10.5;
    public const double MinFontSize = 4, MaxFontSize = 72, MinContentSize = 72;
}
