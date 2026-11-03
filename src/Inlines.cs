using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MdToPdf;

[Flags]
internal enum Style { None = 0, Bold = 1, Italic = 2, Code = 4, Strike = 8, Super = 16 }

/// <summary>A run of uniformly styled text. The text "\n" is a hard line break.
/// With an <paramref name="Image"/>, the text is the alt text to show if the image can't be embedded.</summary>
internal sealed record Inline(string Text, Style Style, string? Link, string? Image = null);

/// <summary>What inline parsing needs from the rest of the document: link and footnote definitions.</summary>
internal sealed class InlineContext
{
    private readonly Dictionary<string, string> links = [];
    private readonly Dictionary<string, List<string>> footnotes = [];
    private readonly Dictionary<string, int> numbers = [];

    /// <summary>The bodies of the referenced footnotes, in order of first reference. It grows as bodies are parsed.</summary>
    public List<List<string>> ReferencedNotes { get; } = [];

    public static string Normalize(string label) => string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>Records a link definition. As in CommonMark, the first one for a label wins.</summary>
    public void DefineLink(string label, string url) => links.TryAdd(Normalize(label), url);

    public void DefineFootnote(string label, List<string> body) => footnotes.TryAdd(Normalize(label), body);

    public string? Link(string label) => links.GetValueOrDefault(Normalize(label));

    /// <summary>The number of a defined footnote, assigned on first reference; null when it isn't defined.</summary>
    public int? FootnoteNumber(string label)
    {
        var key = Normalize(label);
        if (numbers.TryGetValue(key, out var number))
        {
            return number;
        }

        if (!footnotes.TryGetValue(key, out var body))
        {
            return null;
        }

        ReferencedNotes.Add(body);
        return numbers[key] = ReferencedNotes.Count;
    }
}

/// <summary>Inline Markdown parser: emphasis, code, links, images, footnotes, escapes, entities, hard breaks.</summary>
internal static partial class Inlines
{
    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private static readonly Dictionary<string, string> NamedEntities = new()
    {
        ["amp"] = "&",
        ["lt"] = "<",
        ["gt"] = ">",
        ["quot"] = "\"",
        ["apos"] = "'",
        ["nbsp"] = " ",
        ["iexcl"] = "¡",
        ["cent"] = "¢",
        ["pound"] = "£",
        ["curren"] = "¤",
        ["yen"] = "¥",
        ["brvbar"] = "¦",
        ["sect"] = "§",
        ["uml"] = "¨",
        ["copy"] = "©",
        ["ordf"] = "ª",
        ["laquo"] = "«",
        ["not"] = "¬",
        ["shy"] = "­",
        ["reg"] = "®",
        ["macr"] = "¯",
        ["deg"] = "°",
        ["plusmn"] = "±",
        ["sup2"] = "²",
        ["sup3"] = "³",
        ["acute"] = "´",
        ["micro"] = "µ",
        ["para"] = "¶",
        ["middot"] = "·",
        ["cedil"] = "¸",
        ["sup1"] = "¹",
        ["ordm"] = "º",
        ["raquo"] = "»",
        ["frac14"] = "¼",
        ["frac12"] = "½",
        ["frac34"] = "¾",
        ["iquest"] = "¿",
        ["Agrave"] = "À",
        ["Aacute"] = "Á",
        ["Acirc"] = "Â",
        ["Atilde"] = "Ã",
        ["Auml"] = "Ä",
        ["Aring"] = "Å",
        ["AElig"] = "Æ",
        ["Ccedil"] = "Ç",
        ["Egrave"] = "È",
        ["Eacute"] = "É",
        ["Ecirc"] = "Ê",
        ["Euml"] = "Ë",
        ["Igrave"] = "Ì",
        ["Iacute"] = "Í",
        ["Icirc"] = "Î",
        ["Iuml"] = "Ï",
        ["ETH"] = "Ð",
        ["Ntilde"] = "Ñ",
        ["Ograve"] = "Ò",
        ["Oacute"] = "Ó",
        ["Ocirc"] = "Ô",
        ["Otilde"] = "Õ",
        ["Ouml"] = "Ö",
        ["times"] = "×",
        ["Oslash"] = "Ø",
        ["Ugrave"] = "Ù",
        ["Uacute"] = "Ú",
        ["Ucirc"] = "Û",
        ["Uuml"] = "Ü",
        ["Yacute"] = "Ý",
        ["THORN"] = "Þ",
        ["szlig"] = "ß",
        ["agrave"] = "à",
        ["aacute"] = "á",
        ["acirc"] = "â",
        ["atilde"] = "ã",
        ["auml"] = "ä",
        ["aring"] = "å",
        ["aelig"] = "æ",
        ["ccedil"] = "ç",
        ["egrave"] = "è",
        ["eacute"] = "é",
        ["ecirc"] = "ê",
        ["euml"] = "ë",
        ["igrave"] = "ì",
        ["iacute"] = "í",
        ["icirc"] = "î",
        ["iuml"] = "ï",
        ["eth"] = "ð",
        ["ntilde"] = "ñ",
        ["ograve"] = "ò",
        ["oacute"] = "ó",
        ["ocirc"] = "ô",
        ["otilde"] = "õ",
        ["ouml"] = "ö",
        ["divide"] = "÷",
        ["oslash"] = "ø",
        ["ugrave"] = "ù",
        ["uacute"] = "ú",
        ["ucirc"] = "û",
        ["uuml"] = "ü",
        ["yacute"] = "ý",
        ["thorn"] = "þ",
        ["yuml"] = "ÿ",
        ["OElig"] = "Œ",
        ["oelig"] = "œ",
        ["Scaron"] = "Š",
        ["scaron"] = "š",
        ["Yuml"] = "Ÿ",
        ["Zcaron"] = "Ž",
        ["zcaron"] = "ž",
        ["fnof"] = "ƒ",
        ["circ"] = "ˆ",
        ["tilde"] = "˜",
        ["ndash"] = "–",
        ["mdash"] = "—",
        ["lsquo"] = "‘",
        ["rsquo"] = "’",
        ["sbquo"] = "‚",
        ["ldquo"] = "“",
        ["rdquo"] = "”",
        ["bdquo"] = "„",
        ["dagger"] = "†",
        ["Dagger"] = "‡",
        ["bull"] = "•",
        ["hellip"] = "…",
        ["permil"] = "‰",
        ["lsaquo"] = "‹",
        ["rsaquo"] = "›",
        ["euro"] = "€",
        ["trade"] = "™",
        ["larr"] = "←",
        ["rarr"] = "→",
        ["harr"] = "↔",
        ["lArr"] = "⇐",
        ["rArr"] = "⇒",
        ["minus"] = "−",
        ["ne"] = "≠",
        ["le"] = "≤",
        ["ge"] = "≥",
        ["asymp"] = "≈",
        ["ensp"] = " ",
        ["emsp"] = " ",
        ["thinsp"] = " ",
        ["zwnj"] = "‌",
        ["zwj"] = "‍",
    };

    /// <summary>A run of inline text being built; emphasis styles are added once all delimiters are known.</summary>
    private sealed class Piece(string text, Style style, string? link, string? image = null)
    {
        public string Text { get; set; } = text;
        public Style Style { get; set; } = style;
        public string? Link { get; } = link;
        public string? Image { get; } = image;
    }

    /// <summary>A run of emphasis markers on the delimiter stack.</summary>
    private sealed class Delimiter(int piece, char marker, int length, bool canOpen, bool canClose)
    {
        public int Piece { get; } = piece;
        public char Marker { get; } = marker;
        public int Length { get; } = length;
        public int Count { get; set; } = length;
        public bool CanOpen { get; } = canOpen;
        public bool CanClose { get; } = canClose;

        public bool Opens(Delimiter closer) =>
            Marker == closer.Marker && CanOpen && Count > 0
            && !((CanClose || closer.CanOpen) && (Length + closer.Length) % 3 == 0 && !(Length % 3 == 0 && closer.Length % 3 == 0));
    }

    /// <summary>Which closing bracket belongs to each opening one, found once so that scanning stays linear.</summary>
    private sealed class Brackets(string s)
    {
        private int[]? square, round;

        public int[] Square => square ??= Pairs(s, '[', ']');
        public int[] Round => round ??= Pairs(s, '(', ')');

        private static int[] Pairs(string s, char open, char close)
        {
            var pairs = new int[s.Length];
            Array.Fill(pairs, -1);
            var stack = new Stack<int>();
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\')
                {
                    i++;
                }
                else if (s[i] == open)
                {
                    stack.Push(i);
                }
                else if (s[i] == close && stack.Count > 0)
                {
                    pairs[stack.Pop()] = i;
                }
            }

            return pairs;
        }
    }

    public static List<Inline> Parse(string text, InlineContext? context = null)
    {
        var output = new List<Inline>();
        var run = new StringBuilder();
        Piece? first = null;

        void EndRun()
        {
            if (first is not null)
            {
                output.Add(new Inline(run.ToString(), first.Style, first.Link));
                run.Clear();
                first = null;
            }
        }

        // Literal markers were separate pieces during scanning; join them back to their text.
        foreach (var piece in Scan(text.Trim(), null, 0, context ?? new InlineContext()))
        {
            if (piece.Text.Length == 0)
            {
                continue;
            }

            if (piece.Text == "\n" || piece.Image is not null)
            {
                EndRun();
                output.Add(new Inline(piece.Text, piece.Style, piece.Link, piece.Image));
                continue;
            }

            if (first is not null && (first.Style != piece.Style || first.Link != piece.Link))
            {
                EndRun();
            }

            first ??= piece;
            run.Append(piece.Text);
        }

        EndRun();
        return output;
    }

    public static string PlainText(IEnumerable<Inline> content) =>
        string.Concat(content.Select(i => i.Text == "\n" ? " " : i.Text));

    /// <summary>The target of a footnote reference. '^' can't occur in a heading's slug, so the two never collide.</summary>
    public static string FootnoteAnchor(int number) => "^" + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Recognizes text that is nothing but one image.</summary>
    public static bool TryImageOnly(string text, InlineContext context, out string source, out string alt)
    {
        source = alt = "";
        return text.StartsWith("![", StringComparison.Ordinal)
            && TryLink(text, 1, new Brackets(text), context, out alt, out source, out var end)
            && end == text.Length;
    }

    private static List<Piece> Scan(string s, string? link, int depth, InlineContext context)
    {
        var pieces = new List<Piece>();
        var delimiters = new List<Delimiter>();
        var brackets = new Brackets(s);
        var noCodeClose = new HashSet<int>();
        var sb = new StringBuilder();

        void Emit()
        {
            if (sb.Length > 0)
            {
                pieces.Add(new Piece(sb.ToString(), Style.None, link));
                sb.Clear();
            }
        }

        void Break()
        {
            Emit();
            pieces.Add(new Piece("\n", Style.None, link));
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            Match m;

            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '\n')
            {
                Break();
                i += 2;
            }
            else if (c == '\\' && i + 1 < s.Length && Punctuation.Contains(s[i + 1], StringComparison.Ordinal))
            {
                sb.Append(s[i + 1]);
                i += 2;
            }
            else if (c == '\n')
            {
                var spaces = 0;
                while (sb.Length > 0 && sb[^1] == ' ')
                {
                    sb.Length--;
                    spaces++;
                }

                if (spaces >= 2)
                {
                    Break();
                }
                else
                {
                    sb.Append(' ');
                }

                for (i++; i < s.Length && s[i] == ' '; i++)
                {
                }
            }
            else if (c == '`')
            {
                var run = RunLength(s, i, '`');
                var close = noCodeClose.Contains(run) ? -1 : FindCodeClose(s, i + run, run);
                if (close < 0)
                {
                    noCodeClose.Add(run); // later runs of this length have even less text to search
                    sb.Append('`', run);
                    i += run;
                    continue;
                }

                Emit();
                var code = s[(i + run)..close].Replace('\n', ' ');
                if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ')
                {
                    code = code[1..^1];
                }

                pieces.Add(new Piece(code, Style.Code, link));
                i = close + run;
            }
            else if (c == '!' && i + 1 < s.Length && s[i + 1] == '[' && TryLink(s, i + 1, brackets, context, out var alt, out var source, out var imageEnd))
            {
                Emit();
                pieces.Add(new Piece(alt, Style.Italic, link, source));
                i = imageEnd;
            }
            else if (c == '[' && i + 1 < s.Length && s[i + 1] == '^' && brackets.Square[i] > 0
                && context.FootnoteNumber(s[(i + 2)..brackets.Square[i]]) is { } number)
            {
                Emit();
                pieces.Add(new Piece(number.ToString(CultureInfo.InvariantCulture), Style.Super, "#" + FootnoteAnchor(number)));
                i = brackets.Square[i] + 1;
            }
            else if (c == '[' && depth < Limits.MaxInlineDepth && TryLink(s, i, brackets, context, out var label, out var url, out var end))
            {
                Emit();
                pieces.AddRange(Scan(label, url, depth + 1, context));
                i = end;
            }
            else if (c == '<' && string.CompareOrdinal(s, i, "<!--", 0, 4) == 0 && s.IndexOf("-->", i + 4, StringComparison.Ordinal) is >= 0 and var commentEnd)
            {
                i = commentEnd + 3;
            }
            else if (c == '<' && (m = AutoLink().Match(s, i)).Success)
            {
                Emit();
                var target = m.Groups[1].Value;
                var isMail = target.Contains('@', StringComparison.Ordinal) && !target.Contains(':', StringComparison.Ordinal);
                pieces.Add(new Piece(target, Style.None, isMail ? "mailto:" + target : target));
                i += m.Length;
            }
            else if (c == '<' && (m = BreakTag().Match(s, i)).Success)
            {
                Break();
                i += m.Length;
            }
            else if (c == 'h' && link is null && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && (m = BareUrl().Match(s, i)).Success)
            {
                Emit();
                pieces.Add(new Piece(m.Value, Style.None, m.Value));
                i += m.Length;
            }
            else if (c == '&' && (m = Entity().Match(s, i)).Success)
            {
                sb.Append(DecodeEntity(m));
                i += m.Length;
            }
            else if (c is '*' or '_' or '~')
            {
                var run = RunLength(s, i, c);
                var (canOpen, canClose) = Flanking(s, i, run, c);
                if ((canOpen || canClose) && (c != '~' || run == 2))
                {
                    Emit();
                    delimiters.Add(new Delimiter(pieces.Count, c, run, canOpen, canClose));
                    pieces.Add(new Piece(new string(c, run), Style.None, link));
                }
                else
                {
                    sb.Append(c, run);
                }

                i += run;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        Emit();
        ResolveEmphasis(pieces, delimiters);
        return pieces;
    }

    /// <summary>The CommonMark left- and right-flanking rules: whether a marker run can open and close emphasis.</summary>
    private static (bool CanOpen, bool CanClose) Flanking(string s, int i, int run, char c)
    {
        static bool Space(char x) => char.IsWhiteSpace(x);
        static bool Mark(char x) => char.IsPunctuation(x) || char.IsSymbol(x);

        var before = i > 0 ? s[i - 1] : '\n';
        var after = i + run < s.Length ? s[i + run] : '\n';
        var left = !Space(after) && (!Mark(after) || Space(before) || Mark(before));
        var right = !Space(before) && (!Mark(before) || Space(after) || Mark(after));
        return c == '_' ? (left && (!right || Mark(before)), right && (!left || Mark(after))) : (left, right);
    }

    /// <summary>The CommonMark delimiter algorithm: pairs each closer with the nearest opener and styles what lies between.
    /// Styles are recorded as deltas and swept once at the end, so deep nesting stays linear.</summary>
    private static void ResolveEmphasis(List<Piece> pieces, List<Delimiter> delimiters)
    {
        if (delimiters.Count == 0)
        {
            return;
        }

        Style[] flags = [Style.Bold, Style.Italic, Style.Strike];
        var deltas = flags.Select(_ => new int[pieces.Count + 1]).ToArray();

        // The lowest index still worth searching, per kind of closer. Without it, unmatched closers rescan the whole stack.
        var bottoms = new int[18];
        static int Key(Delimiter d) => ((d.Marker switch { '*' => 0, '_' => 1, _ => 2 }) * 6) + ((d.CanOpen ? 1 : 0) * 3) + (d.Length % 3);

        for (var c = 0; c < delimiters.Count;)
        {
            var closer = delimiters[c];
            if (!closer.CanClose || closer.Count == 0)
            {
                c++;
                continue;
            }

            var key = Key(closer);
            var o = c - 1;
            while (o >= bottoms[key] && !delimiters[o].Opens(closer))
            {
                o--;
            }

            if (o < bottoms[key])
            {
                bottoms[key] = c;
                c++;
                continue;
            }

            var opener = delimiters[o];
            var used = closer.Marker == '~' || (opener.Count >= 2 && closer.Count >= 2) ? 2 : 1;
            var flag = closer.Marker == '~' ? 2 : used == 2 ? 0 : 1;
            deltas[flag][opener.Piece + 1]++;
            deltas[flag][closer.Piece]--;

            opener.Count -= used;
            closer.Count -= used;
            pieces[opener.Piece].Text = new string(opener.Marker, opener.Count);
            pieces[closer.Piece].Text = new string(closer.Marker, closer.Count);

            // Markers between a matched pair can no longer match anything.
            delimiters.RemoveRange(o + 1, c - o - 1);
            c = o + 1;
            for (var k = 0; k < bottoms.Length; k++)
            {
                bottoms[k] = Math.Min(bottoms[k], c);
            }
        }

        for (var f = 0; f < flags.Length; f++)
        {
            var active = 0;
            for (var p = 0; p < pieces.Count; p++)
            {
                active += deltas[f][p];
                if (active > 0)
                {
                    pieces[p].Style |= flags[f];
                }
            }
        }
    }

    private static int RunLength(string s, int i, char c)
    {
        var n = 0;
        while (i + n < s.Length && s[i + n] == c)
        {
            n++;
        }

        return n;
    }

    private static int FindCodeClose(string s, int from, int run)
    {
        for (var j = from; j < s.Length; j++)
        {
            if (s[j] == '`')
            {
                var r = RunLength(s, j, '`');
                if (r == run)
                {
                    return j;
                }

                j += r - 1;
            }
        }

        return -1;
    }

    /// <summary>Parses <c>[label](url "title")</c>, <c>[label][ref]</c>, <c>[label][]</c> or <c>[label]</c> starting at the '['.</summary>
    private static bool TryLink(string s, int i, Brackets brackets, InlineContext context, out string label, out string url, out int end)
    {
        label = url = "";
        end = 0;

        var close = brackets.Square[i];
        if (close < 0)
        {
            return false;
        }

        label = s[(i + 1)..close];
        var next = close + 1;
        if (next < s.Length && s[next] == '(' && brackets.Round[next] > 0)
        {
            url = Destination(s[(next + 1)..brackets.Round[next]]);
            end = brackets.Round[next] + 1;
            return true;
        }

        var key = label;
        end = next;
        if (next < s.Length && s[next] == '[' && brackets.Square[next] > 0)
        {
            var reference = s[(next + 1)..brackets.Square[next]];
            key = reference.Length > 0 ? reference : label;
            end = brackets.Square[next] + 1;
        }

        url = context.Link(key) ?? "";
        return url.Length > 0;
    }

    /// <summary>The target in a link's parentheses: spaces only inside &lt;...&gt;, a title after it is dropped.</summary>
    private static string Destination(string raw)
    {
        var dest = raw.Trim();
        if (dest.StartsWith('<') && dest.IndexOf('>', StringComparison.Ordinal) is > 0 and var closing)
        {
            return Unescape(dest[1..closing]);
        }

        var space = dest.IndexOfAny([' ', '\n']);
        return Unescape(space >= 0 ? dest[..space] : dest);
    }

    /// <summary>Applies backslash escapes and entities, as CommonMark does in link destinations.</summary>
    public static string Unescape(string s)
    {
        if (!s.Contains('\\', StringComparison.Ordinal) && !s.Contains('&', StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length;)
        {
            Match m;
            if (s[i] == '\\' && i + 1 < s.Length && Punctuation.Contains(s[i + 1], StringComparison.Ordinal))
            {
                sb.Append(s[i + 1]);
                i += 2;
            }
            else if (s[i] == '&' && (m = Entity().Match(s, i)).Success)
            {
                sb.Append(DecodeEntity(m));
                i += m.Length;
            }
            else
            {
                sb.Append(s[i++]);
            }
        }

        return sb.ToString();
    }

    private static string DecodeEntity(Match m)
    {
        var name = m.Groups[1].Value;
        if (name[0] != '#')
        {
            return NamedEntities.TryGetValue(name, out var value) ? value : m.Value;
        }

        var ok = name[1] is 'x' or 'X'
            ? int.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
            : int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out cp);
        if (!ok || cp is <= 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
        {
            return "?";
        }

        // As in HTML, the C1 range means the Windows-1252 characters at those codes.
        return cp is >= 0x80 and <= 0x9F ? Fonts.FromWindows1252((byte)cp).ToString() : char.ConvertFromUtf32(cp);
    }

    [GeneratedRegex(@"\G<((?:https?|ftp)://[^>\s]+|mailto:[^>\s]+|[^@<>\s]+@[^@<>\s]+\.[^@<>\s]+)>")]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"\G<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"\Ghttps?://[^\s<]*[^\s<.,:;!?'"")\]*_~]")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"\G&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[a-zA-Z][a-zA-Z0-9]{1,7});")]
    private static partial Regex Entity();
}
