using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MdToPdf;

[Flags]
internal enum Style { None = 0, Bold = 1, Italic = 2, Code = 4, Strike = 8 }

/// <summary>A run of uniformly styled text. The text "\n" is a hard line break.</summary>
internal sealed record Inline(string Text, Style Style, string? Link);

/// <summary>Inline Markdown parser: emphasis, code, links, autolinks, escapes, entities, hard breaks.</summary>
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
        ["copy"] = "©",
        ["reg"] = "®",
        ["trade"] = "™",
        ["hellip"] = "…",
        ["mdash"] = "—",
        ["ndash"] = "–",
        ["laquo"] = "«",
        ["raquo"] = "»",
        ["euro"] = "€",
        ["deg"] = "°",
        ["times"] = "×",
    };

    public static List<Inline> Parse(string text)
    {
        var output = new List<Inline>();
        Parse(text.Trim(), Style.None, null, output);
        return output;
    }

    public static string PlainText(IEnumerable<Inline> content) =>
        string.Concat(content.Select(i => i.Text == "\n" ? " " : i.Text));

    private static void Parse(string s, Style style, string? link, List<Inline> output)
    {
        var sb = new StringBuilder();

        void Emit()
        {
            if (sb.Length > 0)
            {
                output.Add(new Inline(sb.ToString(), style, link));
                sb.Clear();
            }
        }

        void Break()
        {
            Emit();
            output.Add(new Inline("\n", style, link));
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
                var close = FindCodeClose(s, i + run, run);
                if (close < 0)
                {
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

                output.Add(new Inline(code, style | Style.Code, link));
                i = close + run;
            }
            else if (c == '!' && i + 1 < s.Length && s[i + 1] == '[' && TryLink(s, i + 1, out var alt, out _, out var imageEnd))
            {
                Emit(); // inline images are not embedded; show the alt text instead
                output.Add(new Inline(alt, style | Style.Italic, link));
                i = imageEnd;
            }
            else if (c == '[' && TryLink(s, i, out var label, out var url, out var end))
            {
                Emit();
                Parse(label, style, url, output);
                i = end;
            }
            else if (c == '<' && (m = AutoLink().Match(s, i)).Success)
            {
                Emit();
                var target = m.Groups[1].Value;
                var isMail = target.Contains('@', StringComparison.Ordinal) && !target.Contains(':', StringComparison.Ordinal);
                output.Add(new Inline(target, style, isMail ? "mailto:" + target : target));
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
                output.Add(new Inline(m.Value, style, m.Value));
                i += m.Length;
            }
            else if (c == '&' && (m = Entity().Match(s, i)).Success)
            {
                sb.Append(DecodeEntity(m));
                i += m.Length;
            }
            else if (c is '*' or '_' or '~')
            {
                if (TryEmphasis(s, i, out var innerEnd, out var delim, out var add))
                {
                    Emit();
                    Parse(s[(i + delim)..innerEnd], style | add, link, output);
                    i = innerEnd + delim;
                }
                else
                {
                    var run = RunLength(s, i, c);
                    sb.Append(c, run);
                    i += run;
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        Emit();
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

    private static bool TryEmphasis(string s, int i, out int innerEnd, out int delim, out Style add)
    {
        var c = s[i];
        var run = RunLength(s, i, c);
        innerEnd = delim = 0;
        add = Style.None;

        if (i + run >= s.Length || char.IsWhiteSpace(s[i + run]) || (c == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1])))
        {
            return false;
        }

        for (var d = Math.Min(run, 3); d >= 1; d--)
        {
            if (c == '~' && d != 2)
            {
                continue;
            }

            var close = FindCloser(s, i + d, c, d);
            if (close >= 0)
            {
                innerEnd = close;
                delim = d;
                add = c == '~' ? Style.Strike : d switch { 3 => Style.Bold | Style.Italic, 2 => Style.Bold, _ => Style.Italic };
                return true;
            }
        }

        return false;
    }

    private static int FindCloser(string s, int from, char c, int d)
    {
        for (var j = from; j < s.Length; j++)
        {
            var x = s[j];
            if (x == '\\')
            {
                j++;
            }
            else if (x == '`')
            {
                var r = RunLength(s, j, '`');
                var close = FindCodeClose(s, j + r, r);
                j = (close >= 0 ? close + r : j + r) - 1;
            }
            else if (x == c)
            {
                var run = RunLength(s, j, c);
                if (run == d && j > from && !char.IsWhiteSpace(s[j - 1])
                    && (c != '_' || j + run >= s.Length || !char.IsLetterOrDigit(s[j + run])))
                {
                    return j;
                }

                j += run - 1;
            }
        }

        return -1;
    }

    /// <summary>Parses <c>[label](url "title")</c> starting at the '['.</summary>
    private static bool TryLink(string s, int i, out string label, out string url, out int end)
    {
        label = url = "";
        end = 0;

        var depth = 0;
        var j = i;
        for (; j < s.Length; j++)
        {
            if (s[j] == '\\')
            {
                j++;
            }
            else if (s[j] == '[')
            {
                depth++;
            }
            else if (s[j] == ']' && --depth == 0)
            {
                break;
            }
        }

        if (j >= s.Length - 1 || s[j + 1] != '(')
        {
            return false;
        }

        depth = 1;
        var k = j + 2;
        for (; k < s.Length; k++)
        {
            if (s[k] == '\\')
            {
                k++;
            }
            else if (s[k] == '(')
            {
                depth++;
            }
            else if (s[k] == ')' && --depth == 0)
            {
                break;
            }
        }

        if (k >= s.Length)
        {
            return false;
        }

        var dest = s[(j + 2)..k].Trim();
        var space = dest.IndexOfAny([' ', '\n']);
        url = (space >= 0 ? dest[..space] : dest).Trim('<', '>');
        label = s[(i + 1)..j];
        end = k + 1;
        return true;
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
        return ok && cp is > 0 and <= 0xFFFF and not (>= 0xD800 and <= 0xDFFF) ? ((char)cp).ToString() : "?";
    }

    [GeneratedRegex(@"\G<((?:https?|ftp)://[^>\s]+|mailto:[^>\s]+|[^@<>\s]+@[^@<>\s]+\.[^@<>\s]+)>")]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"\G<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"\Ghttps?://[^\s<]*[^\s<.,:;!?'"")\]*_~]")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"\G&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[a-zA-Z]{2,8});")]
    private static partial Regex Entity();
}
