using System.Diagnostics.CodeAnalysis;

namespace PkgLens.Core.Shared.Sfo;

/// <summary>Format of an SFO value, from the index-entry <c>data_fmt</c> field.</summary>
public enum SfoFormat : ushort
{
    /// <summary>0x0004 — UTF-8, not null-terminated (special).</summary>
    Utf8Special = 0x0004,

    /// <summary>0x0204 — UTF-8, null-terminated string.</summary>
    Utf8 = 0x0204,

    /// <summary>0x0404 — 32-bit unsigned integer.</summary>
    Int32 = 0x0404,
}

/// <summary>One PARAM.SFO key/value pair.</summary>
public sealed class SfoEntry
{
    public required string Key { get; init; }
    public required SfoFormat Format { get; init; }

    /// <summary>String value for text formats; the decimal form of the integer for <see cref="SfoFormat.Int32"/>.</summary>
    public required string Value { get; init; }

    /// <summary>Present only for <see cref="SfoFormat.Int32"/> entries.</summary>
    public uint? IntValue { get; init; }

    /// <summary>The allocated field size (<c>data_max_len</c>). Preserved so edits keep the layout.</summary>
    public uint MaxLength { get; init; }

    /// <summary>
    /// The exact original value bytes for non-int formats. Preserved so an unedited entry re-serializes
    /// byte-for-byte — <see cref="Value"/> is a possibly-lossy UTF-8 view (binary <c>Utf8Special</c>
    /// payloads decode with U+FFFD). Null for edited entries (the writer then re-encodes from
    /// <see cref="Value"/>) and for <see cref="SfoFormat.Int32"/>.
    /// </summary>
    public byte[]? RawValue { get; init; }

    public bool IsInt => Format == SfoFormat.Int32;

    /// <summary>Returns a copy with a new value (string entries) — used by the SFO editor.</summary>
    /// <remarks>Drops <see cref="RawValue"/>: the value changed, so the writer re-encodes from the string.</remarks>
    public SfoEntry WithValue(string value) =>
        new() { Key = Key, Format = Format, Value = value, IntValue = IntValue, MaxLength = MaxLength };

    /// <summary>Returns a copy with a new integer value (int entries).</summary>
    public SfoEntry WithInt(uint value) =>
        new() { Key = Key, Format = Format, Value = value.ToString(), IntValue = value, MaxLength = MaxLength };
}

/// <summary>A parsed PARAM.SFO: an ordered, case-sensitive key/value table.</summary>
public sealed class SfoTable
{
    private readonly Dictionary<string, SfoEntry> _byKey;

    public IReadOnlyList<SfoEntry> Entries { get; }

    public SfoTable(IReadOnlyList<SfoEntry> entries)
    {
        Entries = entries;
        _byKey = new Dictionary<string, SfoEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
            _byKey[e.Key] = e;
    }

    public bool TryGet(string key, [MaybeNullWhen(false)] out SfoEntry entry) => _byKey.TryGetValue(key, out entry);

    public string? GetString(string key) => _byKey.TryGetValue(key, out var e) ? e.Value : null;

    public uint? GetInt(string key) => _byKey.TryGetValue(key, out var e) ? e.IntValue : null;

    // Convenience accessors for the fields the UI surfaces up top.
    public string? Title => GetString("TITLE");
    public string? TitleId => GetString("TITLE_ID");
    public string? Category => GetString("CATEGORY");
    public string? AppVersion => GetString("APP_VER");
    public string? Version => GetString("VERSION");
    public uint? ParentalLevel => GetInt("PARENTAL_LEVEL");
}
