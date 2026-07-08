namespace PkgLens.Core.Models;

/// <summary>
/// A PS3 content id such as <c>UP0001-NPUB30910_00-EXAMPLE000000001</c>, decomposed into
/// display fields. The raw form is 36 ASCII characters:
/// <code>
/// UP0001 - NPUB30910 _00 - EXAMPLE000000001
/// └─┬──┘   └───┬────┘ └┬┘   └──────┬───────┘
///  region    title-id  variant     internal name
/// </code>
/// </summary>
public sealed record ContentId(string Raw, string? Region, string? TitleId, string? Variant, string? Name)
{
    /// <summary>
    /// Parses a raw content id. Never throws: fields that cannot be located are left null and
    /// <see cref="Raw"/> always preserves the original string (trimmed of trailing nulls).
    /// </summary>
    public static ContentId Parse(string raw)
    {
        raw = (raw ?? string.Empty).TrimEnd('\0').Trim();
        if (raw.Length == 0)
            return new ContentId(string.Empty, null, null, null, null);

        // Split on the two '-' separators: <region><titleid_variant> - ... - <name>.
        // The canonical form has exactly two dashes; be lenient if it does not.
        int firstDash = raw.IndexOf('-');
        int lastDash = raw.LastIndexOf('-');

        if (firstDash < 0 || lastDash == firstDash)
            return new ContentId(raw, null, null, null, null);

        string region = raw[..firstDash];
        string middle = raw[(firstDash + 1)..lastDash]; // e.g. NPUB30910_00
        string name = raw[(lastDash + 1)..];

        string? titleId = middle;
        string? variant = null;
        int underscore = middle.IndexOf('_');
        if (underscore >= 0)
        {
            titleId = middle[..underscore];
            variant = middle[(underscore + 1)..];
        }

        return new ContentId(
            raw,
            region.Length == 0 ? null : region,
            string.IsNullOrEmpty(titleId) ? null : titleId,
            variant,
            name.Length == 0 ? null : name);
    }

    public override string ToString() => Raw;
}
