namespace MdToPdf;

/// <summary>Caps on hostile or broken input, so it degrades instead of overflowing the stack or exhausting memory.</summary>
internal static class Limits
{
    /// <summary>Quote and list levels. Deeper markers are kept as plain text.</summary>
    public const int MaxNesting = 32;

    /// <summary>Link labels nested inside each other. Deeper brackets are kept as plain text.</summary>
    public const int MaxInlineDepth = 16;

    /// <summary>Decoded size of a PNG that has to be unpacked to split off its transparency.</summary>
    public const long MaxDecodedImageBytes = 512L * 1024 * 1024;
}
