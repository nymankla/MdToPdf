using System.Globalization;
using System.Text;

namespace MdToPdf;

/// <summary>Culture-invariant formatting of the values that appear in PDF syntax.</summary>
internal static class PdfSyntax
{
    public static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    public static string Invariant(FormattableString s) => FormattableString.Invariant(s);

    /// <summary>An indirect reference to an object.</summary>
    public static string Ref(int id) => id.ToString(CultureInfo.InvariantCulture) + " 0 R";

    public static string Refs(IEnumerable<int> ids) => string.Join(' ', ids.Select(Ref));

    /// <summary>A PDF literal string in WinAnsi, kept 7-bit by octal-escaping high bytes.</summary>
    public static string Literal(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('(');
        foreach (var c in text)
        {
            var b = Fonts.Encode(c);
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                sb.Append('\\').Append((char)b);
            }
            else if (b > 126)
            {
                sb.Append('\\').Append(Convert.ToString(b, 8));
            }
            else
            {
                sb.Append((char)b);
            }
        }

        return sb.Append(')').ToString();
    }

    /// <summary>A PDF text string (UTF-16BE with BOM) for metadata and bookmarks.</summary>
    public static string TextString(string text)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (var c in text)
        {
            sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.Append('>').ToString();
    }
}
