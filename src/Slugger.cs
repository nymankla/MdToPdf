using System.Text;
using static MdToPdf.PdfSyntax;

namespace MdToPdf;

/// <summary>Turns heading text into link targets the way GitHub does.</summary>
internal sealed class Slugger
{
    private readonly Dictionary<string, int> counts = [];

    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append('-');
            }
        }

        return sb.ToString();
    }

    /// <summary>The slug for <paramref name="text"/>, de-duplicated: the second "intro" becomes "intro-1", the third "intro-2".</summary>
    public string Unique(string text)
    {
        var slug = Slug(text);
        var unique = slug;
        while (counts.ContainsKey(unique))
        {
            counts[slug]++;
            unique = Invariant($"{slug}-{counts[slug]}");
        }

        counts[unique] = 0;
        return unique;
    }
}
