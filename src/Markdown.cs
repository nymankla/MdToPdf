using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MdToPdf;

internal abstract record Block;
internal sealed record Heading(int Level, List<Inline> Content) : Block;
internal sealed record Paragraph(List<Inline> Content) : Block;
internal sealed record CodeBlock(List<string> Lines) : Block;
internal sealed record Quote(List<Block> Children) : Block;
internal sealed record ListBlock(bool Ordered, int Start, List<List<Block>> Items) : Block;
internal sealed record Rule : Block;
internal sealed record Table(List<List<Inline>> Header, List<Align> Aligns, List<List<List<Inline>>> Rows) : Block;
internal sealed record ImageBlock(string Source, string Alt) : Block;

internal enum Align { Left, Center, Right }

/// <summary>Block-level Markdown parser covering the commonly used CommonMark + GFM subset.</summary>
internal static partial class Markdown
{
    public static List<Block> Parse(string text)
    {
        var lines = Sanitize(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return ParseBlocks(lines.Select(ExpandTabs).ToList());
    }

    /// <summary>Composes accents (e + combining acute → é) so they map to single WinAnsi chars, and drops a leading BOM.
    /// Font fallbacks happen later, in <see cref="Fonts.Prepare"/>, so the AST keeps the original text.</summary>
    private static string Sanitize(string text) => text.Normalize(NormalizationForm.FormC).TrimStart('\uFEFF');

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t', StringComparison.Ordinal))
        {
            return line;
        }

        var sb = new StringBuilder();
        foreach (var c in line)
        {
            if (c == '\t')
            {
                sb.Append(' ', 4 - (sb.Length % 4));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static int Indent(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        return i;
    }

    private static bool IsBlank(string line) => line.AsSpan().Trim().IsEmpty;

    private static string StripIndent(string line, int n) => line[Math.Min(n, Indent(line))..];

    private static bool StartsBlock(string line)
    {
        var t = line.Trim();
        return ListItem().IsMatch(line) || Atx().IsMatch(t) || HRule().IsMatch(t) || Fence().IsMatch(line)
            || t.StartsWith('>') || t.StartsWith("<!--", StringComparison.Ordinal);
    }

    private static List<Block> ParseBlocks(List<string> lines)
    {
        var blocks = new List<Block>();
        var para = new List<string>();

        void Flush()
        {
            if (para.Count == 0)
            {
                return;
            }

            var text = string.Join('\n', para);
            var img = ImageOnly().Match(text.Trim());
            blocks.Add(img.Success ? new ImageBlock(img.Groups[2].Value, img.Groups[1].Value) : new Paragraph(Inlines.Parse(text)));
            para.Clear();
        }

        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i];
            var t = line.Trim();
            var indent = Indent(line);
            Match m;

            if (t.Length == 0)
            {
                Flush();
                i++;
            }
            else if (indent >= 4)
            {
                if (para.Count > 0)
                {
                    para.Add(t); // lazy paragraph continuation
                    i++;
                    continue;
                }

                var code = new List<string>();
                for (; i < lines.Count && (Indent(lines[i]) >= 4 || IsBlank(lines[i])); i++)
                {
                    code.Add(StripIndent(lines[i], 4));
                }

                while (code.Count > 0 && IsBlank(code[^1]))
                {
                    code.RemoveAt(code.Count - 1);
                }

                blocks.Add(new CodeBlock(code));
            }
            else if ((m = Fence().Match(line)).Success)
            {
                Flush();
                var fence = m.Groups[2].Value;
                var code = new List<string>();
                for (i++; i < lines.Count && !IsFenceClose(lines[i], fence); i++)
                {
                    code.Add(StripIndent(lines[i], indent));
                }

                i++;
                blocks.Add(new CodeBlock(code));
            }
            else if (t.StartsWith("<!--", StringComparison.Ordinal))
            {
                Flush();
                while (i < lines.Count && !lines[i].Contains("-->", StringComparison.Ordinal))
                {
                    i++;
                }

                i++;
            }
            else if ((m = Atx().Match(t)).Success)
            {
                Flush();
                blocks.Add(new Heading(m.Groups[1].Length, Inlines.Parse(m.Groups[2].Value)));
                i++;
            }
            else if (para.Count > 0 && (m = Setext().Match(t)).Success)
            {
                blocks.Add(new Heading(m.Value[0] == '=' ? 1 : 2, Inlines.Parse(string.Join('\n', para))));
                para.Clear();
                i++;
            }
            else if (HRule().IsMatch(t))
            {
                Flush();
                blocks.Add(new Rule());
                i++;
            }
            else if (t[0] == '>')
            {
                Flush();
                var inner = new List<string>();
                for (; i < lines.Count && !IsBlank(lines[i]); i++)
                {
                    var q = lines[i].TrimStart();
                    if (q[0] == '>')
                    {
                        q = q.Length > 1 && q[1] == ' ' ? q[2..] : q[1..];
                    }

                    inner.Add(q);
                }

                blocks.Add(new Quote(ParseBlocks(inner)));
            }
            else if ((m = ListItem().Match(line)).Success && !(para.Count > 0 && m.Groups[4].Length == 0))
            {
                Flush();
                blocks.Add(ParseList(lines, ref i));
            }
            else if (line.Contains('|', StringComparison.Ordinal) && i + 1 < lines.Count
                && TableDelimiter().IsMatch(lines[i + 1].Trim()) && SplitRow(line).Count == SplitRow(lines[i + 1]).Count)
            {
                Flush();
                blocks.Add(ParseTable(lines, ref i));
            }
            else
            {
                para.Add(line.TrimStart()); // keep trailing spaces: two of them mean a hard break
                i++;
            }
        }

        Flush();
        return blocks;
    }

    private static bool IsFenceClose(string line, string fence)
    {
        var t = line.Trim();
        return t.Length >= fence.Length && t.All(c => c == fence[0]);
    }

    private static bool IsOrdered(Match m) => char.IsAsciiDigit(m.Groups[2].Value[0]);

    private static ListBlock ParseList(List<string> lines, ref int i)
    {
        var first = ListItem().Match(lines[i]);
        var ordered = IsOrdered(first);
        var start = ordered ? int.Parse(first.Groups[2].Value[..^1], CultureInfo.InvariantCulture) : 1;
        var items = new List<List<Block>>();

        while (true)
        {
            var m = ListItem().Match(lines[i]);
            var spaces = m.Groups[3].Length;
            var contentIndent = m.Groups[1].Length + m.Groups[2].Length + (spaces is >= 1 and <= 4 ? spaces : 1);
            var body = new List<string> { m.Groups[4].Value };

            for (i++; i < lines.Count; i++)
            {
                var l = lines[i];
                if (IsBlank(l))
                {
                    var next = i + 1;
                    while (next < lines.Count && IsBlank(lines[next]))
                    {
                        next++;
                    }

                    if (next < lines.Count && Indent(lines[next]) >= contentIndent)
                    {
                        body.Add("");
                        continue;
                    }

                    break;
                }

                if (Indent(l) >= contentIndent)
                {
                    body.Add(l[contentIndent..]);
                }
                else if (!IsBlank(body[^1]) && !StartsBlock(l))
                {
                    body.Add(l.TrimStart()); // lazy continuation
                }
                else
                {
                    break;
                }
            }

            items.Add(ParseBlocks(body));

            // The list continues if the next non-blank line is a sibling item of the same kind.
            var j = i;
            while (j < lines.Count && IsBlank(lines[j]))
            {
                j++;
            }

            if (j < lines.Count && ListItem().Match(lines[j]) is { Success: true } n && IsOrdered(n) == ordered && Indent(lines[j]) < contentIndent)
            {
                i = j;
            }
            else
            {
                return new ListBlock(ordered, start, items);
            }
        }
    }

    private static Table ParseTable(List<string> lines, ref int i)
    {
        var header = SplitRow(lines[i]);
        var aligns = SplitRow(lines[i + 1])
            .Select(c => (c.StartsWith(':'), c.EndsWith(':')) switch
            {
                (true, true) => Align.Center,
                (false, true) => Align.Right,
                _ => Align.Left,
            })
            .ToList();

        var rows = new List<List<List<Inline>>>();
        for (i += 2; i < lines.Count && lines[i].Contains('|', StringComparison.Ordinal); i++)
        {
            var cells = SplitRow(lines[i]);
            rows.Add(Enumerable.Range(0, header.Count).Select(c => Inlines.Parse(c < cells.Count ? cells[c] : "")).ToList());
        }

        return new Table(header.Select(Inlines.Parse).ToList(), aligns, rows);
    }

    private static List<string> SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|'))
        {
            t = t[1..];
        }

        if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal))
        {
            t = t[..^1];
        }

        var cells = new List<string>();
        var sb = new StringBuilder();
        for (var k = 0; k < t.Length; k++)
        {
            if (t[k] == '\\' && k + 1 < t.Length && t[k + 1] == '|')
            {
                sb.Append('|');
                k++;
            }
            else if (t[k] == '|')
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(t[k]);
            }
        }

        cells.Add(sb.ToString().Trim());
        return cells;
    }

    [GeneratedRegex(@"^(#{1,6})(?:\s+(.*?))?(?:\s+#+)?\s*$")]
    private static partial Regex Atx();

    [GeneratedRegex(@"^(?:=+|-+)$")]
    private static partial Regex Setext();

    [GeneratedRegex(@"^(?:(?:-\s*){3,}|(?:\*\s*){3,}|(?:_\s*){3,})$")]
    private static partial Regex HRule();

    [GeneratedRegex(@"^( {0,3})(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^( *)([-*+]|\d{1,9}[.)])( +|$)(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?$")]
    private static partial Regex TableDelimiter();

    [GeneratedRegex(@"^!\[([^\]]*)\]\(\s*<?([^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)$")]
    private static partial Regex ImageOnly();
}
