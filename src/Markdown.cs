using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MdToPdf;

internal abstract record Block;
internal sealed record Heading(int Level, List<Inline> Content) : Block;
internal sealed record Paragraph(List<Inline> Content) : Block;
internal sealed record CodeBlock(List<string> Lines) : Block;
internal sealed record Quote(List<Block> Children) : Block;

/// <summary>A list. Tasks holds, per item, true or false for a checkbox and null for an ordinary item; it is null when no item has one.</summary>
internal sealed record ListBlock(bool Ordered, int Start, List<List<Block>> Items, List<bool?>? Tasks = null) : Block;
internal sealed record Rule : Block;
internal sealed record Table(List<List<Inline>> Header, List<Align> Aligns, List<List<List<Inline>>> Rows) : Block;
internal sealed record ImageBlock(string Source, string Alt) : Block;

/// <summary>The referenced footnotes, numbered from 1 in list order. Always the last block of a document.</summary>
internal sealed record FootnoteList(List<List<Block>> Notes) : Block;

internal enum Align { Left, Center, Right }

/// <summary>A parsed file: its blocks, and what YAML front matter said about it.</summary>
internal sealed record Document(List<Block> Blocks, string? Title, string? Author);

/// <summary>Block-level Markdown parser covering the commonly used CommonMark + GFM subset.</summary>
internal static partial class Markdown
{
    public static List<Block> Parse(string text) => ParseDocument(text).Blocks;

    public static Document ParseDocument(string text)
    {
        var lines = Sanitize(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(ExpandTabs).ToList();
        var (title, author) = FrontMatter(lines);

        // Definitions may follow their uses, so they are collected before any inline is parsed.
        var context = new InlineContext();
        var blocks = ParseBlocks(CollectDefinitions(lines, context), context, 0);

        var notes = new List<List<Block>>();
        for (var k = 0; k < context.ReferencedNotes.Count; k++) // a note may refer to further notes
        {
            notes.Add(ParseBlocks(context.ReferencedNotes[k], context, 0));
        }

        if (notes.Count > 0)
        {
            blocks.Add(new FootnoteList(notes));
        }

        return new Document(blocks, title, author);
    }

    /// <summary>Composes accents (e + combining acute → é) so they map to single WinAnsi chars, and drops a leading BOM.
    /// Font fallbacks happen later, in <see cref="Fonts.Prepare"/>, so the AST keeps the original text.</summary>
    private static string Sanitize(string text) => Fonts.Compose(text).TrimStart('﻿');

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

    /// <summary>The index of the first non-blank line at or after <paramref name="i"/>; the line count when there is none.</summary>
    private static int SkipBlank(List<string> lines, int i)
    {
        while (i < lines.Count && IsBlank(lines[i]))
        {
            i++;
        }

        return i;
    }

    private static bool StartsBlock(string line, bool anyListItem = true)
    {
        var t = line.Trim();
        return (ListItem().Match(line) is { Success: true } m && (anyListItem || CanInterruptParagraph(m)))
            || Atx().IsMatch(t) || HRule().IsMatch(t) || Fence().IsMatch(line)
            || t.StartsWith('>') || t.StartsWith("<!--", StringComparison.Ordinal);
    }

    /// <summary>As in CommonMark: an empty item can't start a list inside a paragraph, and an ordered one only if it counts from 1,
    /// so that a wrapped line starting "1986. A great year" stays text.</summary>
    private static bool CanInterruptParagraph(Match item) =>
        item.Groups[4].Length > 0 && (!IsOrdered(item) || Number(item) == 1);

    // ---------------------------------------------------------------- front matter and definitions

    /// <summary>Removes a leading YAML block of "key: value" lines and returns its title and author. Only that flat form
    /// counts: anything else between two rules is ordinary Markdown.</summary>
    private static (string? Title, string? Author) FrontMatter(List<string> lines)
    {
        if (lines.Count < 3 || lines[0].TrimEnd() != "---")
        {
            return (null, null);
        }

        var close = lines.FindIndex(1, l => l.TrimEnd() is "---" or "...");
        if (close < 2 || !lines.GetRange(1, close - 1).All(l => IsBlank(l) || l[0] is ' ' or '#' or '-' || FrontMatterEntry().IsMatch(l)) || !FrontMatterEntry().IsMatch(lines[1]))
        {
            return (null, null);
        }

        string? Value(string key)
        {
            foreach (var line in lines.GetRange(1, close - 1))
            {
                var m = FrontMatterEntry().Match(line);
                if (m.Success && m.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    var value = m.Groups[2].Value.Trim();
                    var quoted = value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0];
                    value = quoted ? value[1..^1] : value;
                    return value.Length > 0 ? value : null;
                }
            }

            return null;
        }

        var result = (Value("title"), Value("author"));
        lines.RemoveRange(0, close + 1);
        return result;
    }

    /// <summary>Moves top-level link and footnote definitions out of the text into <paramref name="context"/>.
    /// As in CommonMark, a definition can't interrupt a paragraph, and fenced code is left alone.</summary>
    private static List<string> CollectDefinitions(List<string> lines, InlineContext context)
    {
        var kept = new List<string>();
        string? fence = null;
        var afterDefinition = true;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (fence is not null)
            {
                fence = IsFenceClose(line, fence) ? null : fence;
            }
            else if (Fence().Match(line) is { Success: true } opening)
            {
                fence = opening.Groups[2].Value;
            }
            else if (afterDefinition || kept.Count == 0 || IsBlank(kept[^1]))
            {
                Match m;
                if ((m = FootnoteDefinition().Match(line)).Success)
                {
                    context.DefineFootnote(m.Groups[1].Value, FootnoteBody(lines, ref i, m.Groups[2].Value));
                    afterDefinition = true;
                    continue;
                }

                if ((m = LinkDefinition().Match(line)).Success && InlineContext.Normalize(m.Groups[1].Value).Length > 0)
                {
                    context.DefineLink(m.Groups[1].Value, Inlines.Unescape(m.Groups[2].Value.Trim('<', '>')));
                    afterDefinition = true;
                    continue;
                }
            }

            afterDefinition = false;
            kept.Add(line);
        }

        return kept;
    }

    /// <summary>A note's first line plus its indented or lazy continuation lines; <paramref name="i"/> ends on the last of them.</summary>
    private static List<string> FootnoteBody(List<string> lines, ref int i, string first)
    {
        var body = new List<string> { first };
        for (; i + 1 < lines.Count; i++)
        {
            var next = lines[i + 1];
            if (IsBlank(next))
            {
                var after = SkipBlank(lines, i + 1);
                if (after == lines.Count || Indent(lines[after]) < 4)
                {
                    break;
                }

                body.Add("");
            }
            else if (Indent(next) >= 4)
            {
                body.Add(next[4..]);
            }
            else if (!IsBlank(body[^1]) && !StartsBlock(next) && !FootnoteDefinition().IsMatch(next) && !LinkDefinition().IsMatch(next))
            {
                body.Add(next.TrimStart());
            }
            else
            {
                break;
            }
        }

        return body;
    }

    // ---------------------------------------------------------------- blocks

    private static List<Block> ParseBlocks(List<string> lines, InlineContext context, int depth)
    {
        var blocks = new List<Block>();
        var para = new List<string>();
        var canNest = depth < Limits.MaxNesting;

        void Flush()
        {
            if (para.Count == 0)
            {
                return;
            }

            var text = string.Join('\n', para);
            blocks.Add(Inlines.TryImageOnly(text.Trim(), context, out var source, out var alt)
                ? new ImageBlock(source, alt)
                : new Paragraph(Inlines.Parse(text, context)));
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
                var close = -1;
                while (i < lines.Count && (close = lines[i].IndexOf("-->", StringComparison.Ordinal)) < 0)
                {
                    i++;
                }

                // Text after the end of the comment is still content.
                if (i < lines.Count && !IsBlank(lines[i][(close + 3)..]))
                {
                    lines[i] = lines[i][(close + 3)..];
                }
                else
                {
                    i++;
                }
            }
            else if ((m = Atx().Match(t)).Success)
            {
                Flush();
                blocks.Add(new Heading(m.Groups[1].Length, Inlines.Parse(m.Groups[2].Value, context)));
                i++;
            }
            else if (para.Count > 0 && (m = Setext().Match(t)).Success)
            {
                blocks.Add(new Heading(m.Value[0] == '=' ? 1 : 2, Inlines.Parse(string.Join('\n', para), context)));
                para.Clear();
                i++;
            }
            else if (HRule().IsMatch(t))
            {
                Flush();
                blocks.Add(new Rule());
                i++;
            }
            else if (t[0] == '>' && canNest)
            {
                Flush();
                blocks.Add(new Quote(ParseBlocks(QuoteLines(lines, ref i), context, depth + 1)));
            }
            else if ((m = ListItem().Match(line)).Success && canNest && (para.Count == 0 || CanInterruptParagraph(m)))
            {
                Flush();
                blocks.Add(ParseList(lines, ref i, context, depth));
            }
            else if (line.Contains('|', StringComparison.Ordinal) && i + 1 < lines.Count
                && TableDelimiter().IsMatch(lines[i + 1].Trim()) && SplitRow(line).Count == SplitRow(lines[i + 1]).Count)
            {
                Flush();
                blocks.Add(ParseTable(lines, ref i, context));
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

    /// <summary>The content of a block quote. A line without '>' continues it only as a lazy paragraph line.</summary>
    private static List<string> QuoteLines(List<string> lines, ref int i)
    {
        var inner = new List<string>();
        for (; i < lines.Count && !IsBlank(lines[i]); i++)
        {
            var q = lines[i].TrimStart();
            if (q[0] == '>')
            {
                q = q.Length > 1 && q[1] == ' ' ? q[2..] : q[1..];
            }
            else if (IsBlank(inner[^1]) || StartsBlock(lines[i], anyListItem: false))
            {
                break;
            }

            inner.Add(q);
        }

        return inner;
    }

    private static bool IsFenceClose(string line, string fence)
    {
        var t = line.Trim();
        return t.Length >= fence.Length && t.All(c => c == fence[0]);
    }

    private static bool IsOrdered(Match m) => char.IsAsciiDigit(m.Groups[2].Value[0]);

    private static int Number(Match m) => int.Parse(m.Groups[2].Value[..^1], CultureInfo.InvariantCulture);

    private static ListBlock ParseList(List<string> lines, ref int i, InlineContext context, int depth)
    {
        var first = ListItem().Match(lines[i]);
        var ordered = IsOrdered(first);
        var start = ordered ? Number(first) : 1;
        var items = new List<List<Block>>();
        var tasks = new List<bool?>();

        while (true)
        {
            var m = ListItem().Match(lines[i]);
            var spaces = m.Groups[3].Length;
            var contentIndent = m.Groups[1].Length + m.Groups[2].Length + (spaces is >= 1 and <= 4 ? spaces : 1);
            var task = TaskItem().Match(m.Groups[4].Value);
            tasks.Add(task.Success ? task.Groups[1].Value != " " : null);
            var body = new List<string> { task.Success ? task.Groups[2].Value : m.Groups[4].Value };

            for (i++; i < lines.Count; i++)
            {
                var l = lines[i];
                if (IsBlank(l))
                {
                    var next = SkipBlank(lines, i);
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

            items.Add(ParseBlocks(body, context, depth + 1));

            // The list continues if the next non-blank line is a sibling item of the same kind.
            var j = SkipBlank(lines, i);
            if (j < lines.Count && ListItem().Match(lines[j]) is { Success: true } n && IsOrdered(n) == ordered && Indent(lines[j]) < contentIndent)
            {
                i = j;
            }
            else
            {
                return new ListBlock(ordered, start, items, tasks.Any(x => x is not null) ? tasks : null);
            }
        }
    }

    private static Table ParseTable(List<string> lines, ref int i, InlineContext context)
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
            rows.Add(Enumerable.Range(0, header.Count).Select(c => Inlines.Parse(c < cells.Count ? cells[c] : "", context)).ToList());
        }

        return new Table(header.Select(h => Inlines.Parse(h, context)).ToList(), aligns, rows);
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

    [GeneratedRegex(@"^\[([ xX])\]\s+(.*)$")]
    private static partial Regex TaskItem();

    [GeneratedRegex(@"^\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?$")]
    private static partial Regex TableDelimiter();

    [GeneratedRegex(@"^([A-Za-z_][\w-]*):(?:\s+(.*))?$")]
    private static partial Regex FrontMatterEntry();

    [GeneratedRegex(@"^ {0,3}\[\^([^\]\s]+)\]:\s*(.*)$")]
    private static partial Regex FootnoteDefinition();

    [GeneratedRegex(@"^ {0,3}\[(?!\^)((?:[^\]\\]|\\.)+)\]:\s*(<[^>\n]*>|\S+)(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*$")]
    private static partial Regex LinkDefinition();
}
