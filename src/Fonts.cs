namespace MdToPdf;

/// <summary>The standard PDF fonts used by the renderer; the value is the resource index (/F0../F4).</summary>
internal enum FontId { Regular, Bold, Italic, BoldItalic, Mono }

/// <summary>Metrics and WinAnsi encoding for the built-in (non-embedded) PDF Type 1 fonts.</summary>
internal static class Fonts
{
    public static readonly string[] BaseNames = ["Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique", "Courier"];

    // Advance widths in 1/1000 em for WinAnsi codes 32..255 (from the Adobe AFM files).
    // Oblique variants share the upright widths; Courier is fixed at 600.
    private static readonly short[] Helvetica =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 0,
        556, 0, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0,
        0, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 0, 500, 667,
        278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333,
        400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365, 556, 834, 834, 834, 611,
        667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
        556, 556, 556, 556, 556, 556, 889, 500, 556, 556, 556, 556, 278, 278, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556, 500, 556, 500,
    ];

    private static readonly short[] HelveticaBold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584, 0,
        556, 0, 278, 556, 500, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0,
        0, 278, 278, 500, 500, 350, 556, 1000, 333, 1000, 556, 333, 944, 0, 500, 667,
        278, 333, 556, 556, 556, 556, 280, 556, 333, 737, 370, 556, 584, 333, 737, 333,
        400, 584, 333, 333, 333, 611, 556, 278, 333, 333, 365, 556, 834, 834, 834, 611,
        722, 722, 722, 722, 722, 722, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
        556, 556, 556, 556, 556, 556, 889, 556, 556, 556, 556, 556, 278, 278, 278, 278,
        611, 611, 611, 611, 611, 611, 611, 584, 611, 611, 611, 611, 611, 556, 611, 556,
    ];

    // WinAnsi 128..159 differs from Latin-1; these are the Unicode chars living there.
    private static readonly Dictionary<char, byte> WinAnsiExtras = new()
    {
        ['€'] = 128,
        ['‚'] = 130,
        ['ƒ'] = 131,
        ['„'] = 132,
        ['…'] = 133,
        ['†'] = 134,
        ['‡'] = 135,
        ['ˆ'] = 136,
        ['‰'] = 137,
        ['Š'] = 138,
        ['‹'] = 139,
        ['Œ'] = 140,
        ['Ž'] = 142,
        ['‘'] = 145,
        ['’'] = 146,
        ['“'] = 147,
        ['”'] = 148,
        ['•'] = 149,
        ['–'] = 150,
        ['—'] = 151,
        ['˜'] = 152,
        ['™'] = 153,
        ['š'] = 154,
        ['›'] = 155,
        ['œ'] = 156,
        ['ž'] = 158,
        ['Ÿ'] = 159,
    };

    /// <summary>Maps a char to its WinAnsi byte; anything unrepresentable becomes '?'.</summary>
    public static byte Encode(char c) => c switch
    {
        >= ' ' and <= '~' => (byte)c,
        >= ' ' and <= 'ÿ' => (byte)c,
        _ => WinAnsiExtras.TryGetValue(c, out var b) ? b : (byte)'?',
    };

    public static double Width(string text, FontId font, double size)
    {
        if (font == FontId.Mono)
        {
            return text.Length * 0.6 * size;
        }

        var table = font is FontId.Bold or FontId.BoldItalic ? HelveticaBold : Helvetica;
        var sum = 0;
        foreach (var c in text)
        {
            sum += table[Encode(c) - 32];
        }

        return sum * size / 1000;
    }
}
