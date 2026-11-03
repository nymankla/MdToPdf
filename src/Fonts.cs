using System.Text;

namespace MdToPdf;

/// <summary>The standard PDF fonts used by the renderer; the value is the resource index (/F0../F4).</summary>
internal enum FontId { Regular, Bold, Italic, BoldItalic, Mono, MonoBold }

/// <summary>Metrics and WinAnsi encoding for the built-in (non-embedded) PDF Type 1 fonts.</summary>
internal static class Fonts
{
    public static readonly string[] BaseNames = ["Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique", "Courier", "Courier-Bold"];

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

    // ASCII stand-ins for common symbols the standard fonts lack.
    private static readonly Dictionary<char, string> Fallbacks = new()
    {
        ['→'] = "->",
        ['←'] = "<-",
        ['↔'] = "<->",
        ['⇒'] = "=>",
        ['⇐'] = "<=",
        ['≤'] = "<=",
        ['≥'] = ">=",
        ['≠'] = "!=",
        ['≈'] = "~",
        ['−'] = "-",
        ['✓'] = "x",
        ['✔'] = "x",
        ['✗'] = "-",
        ['✘'] = "-",
        ['☐'] = "[ ]",
        ['☑'] = "[x]",
        ['★'] = "*",
        ['\u2002'] = " ", // en space
        ['\u2003'] = " ", // em space
        ['\u2004'] = " ", // three-per-em space
        ['\u2005'] = " ", // four-per-em space
        ['\u2006'] = " ", // six-per-em space
        ['\u2007'] = "\u00A0", // figure space, which doesn't break
        ['\u2008'] = " ", // punctuation space
        ['\u2009'] = " ", // thin space
        ['\u200A'] = " ", // hair space
        ['\u202F'] = "\u00A0", // narrow no-break space
        ['\u205F'] = " ", // medium mathematical space
        ['\u3000'] = " ", // ideographic space
        ['\u2010'] = "-", // hyphen
        ['\u2011'] = "-", // non-breaking hyphen
        ['\u2012'] = "-", // figure dash
        ['\u2015'] = "—", // horizontal bar
    };

    // Each string pairs a base letter with its composed form, for the combining marks that lead to a WinAnsi letter.
    private static readonly Dictionary<char, string> Compositions = new()
    {
        ['\u0300'] = "AÀEÈIÌOÒUÙaàeèiìoòuù",
        ['\u0301'] = "AÁEÉIÍOÓUÚYÝaáeéiíoóuúyý",
        ['\u0302'] = "AÂEÊIÎOÔUÛaâeêiîoôuû",
        ['\u0303'] = "AÃNÑOÕaãnñoõ",
        ['\u0308'] = "AÄEËIÏOÖUÜYŸaäeëiïoöuüyÿ",
        ['\u030A'] = "AÅaå",
        ['\u030C'] = "SŠZŽsšzž",
        ['\u0327'] = "CÇcç",
    };

    /// <summary>
    /// Composes base letters with combining accents, as files from macOS often have them. <see cref="string.Normalize()"/>
    /// can't do this: under InvariantGlobalization it changes nothing, so the letters that matter here are mapped directly.
    /// </summary>
    public static string Compose(string text)
    {
        if (text.AsSpan().IndexOfAnyInRange('\u0300', '\u036F') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (sb.Length > 0 && Compositions.TryGetValue(c, out var pairs))
            {
                var at = pairs.IndexOf(sb[^1], StringComparison.Ordinal);
                if (at >= 0 && at % 2 == 0)
                {
                    sb[^1] = pairs[at + 1];
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsInvisible(char c) =>
        c is '\u00AD' or (>= '\u200B' and <= '\u200F') or '\u2060' or '\uFE0E' or '\uFE0F' or '\uFEFF' or (>= '\u0300' and <= '\u036F');

    /// <summary>
    /// Rewrites text for drawing with the standard fonts: common symbols get ASCII stand-ins, invisible chars
    /// (soft hyphens, joiners, variation selectors, leftover combining marks, BOM) are dropped and whatever the fonts
    /// lack becomes '?'. Everything that is measured or drawn goes through this; the AST and bookmarks keep the original
    /// Unicode. Each code point lost that way is added to <paramref name="unsupported"/>.
    /// </summary>
    public static string Prepare(string text, ISet<int>? unsupported = null)
    {
        if (text.AsSpan().IndexOfAnyExceptInRange(' ', '~') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                unsupported?.Add(char.ConvertToUtf32(c, text[i]));
                sb.Append('?');
            }
            else if (IsInvisible(c))
            {
                continue;
            }
            else if (Fallbacks.TryGetValue(c, out var fallback))
            {
                sb.Append(fallback);
            }
            else if (Encode(c) == (byte)'?' && c != '?')
            {
                unsupported?.Add(c);
                sb.Append('?');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Maps a char to its WinAnsi byte; anything unrepresentable becomes '?'.</summary>
    public static byte Encode(char c) => c switch
    {
        >= ' ' and <= '~' or >= '\u00A0' and <= 'ÿ' => (byte)c,
        _ => WinAnsiExtras.TryGetValue(c, out var b) ? b : (byte)'?',
    };

    /// <summary>The character a Windows-1252 byte stands for; U+FFFD for the five bytes it leaves undefined.</summary>
    public static char FromWindows1252(byte b)
    {
        if (b is < 0x80 or >= 0xA0)
        {
            return (char)b;
        }

        foreach (var (c, code) in WinAnsiExtras)
        {
            if (code == b)
            {
                return c;
            }
        }

        return '\uFFFD';
    }

    public static double Width(string text, FontId font, double size)
    {
        if (font is FontId.Mono or FontId.MonoBold)
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
