using PkgLens.Core.Sfo;

namespace PkgLens.Core.Models;

/// <summary>
/// The fully-parsed view of a package: header, metadata, item table, and (when available)
/// PARAM.SFO. Header/metadata/content-id are always populated; entries and SFO are present only
/// when a decryptor could be resolved (always for debug packages; for retail packages only when
/// a key was supplied).
/// </summary>
public sealed class PkgInfo
{
    public required PkgHeader Header { get; init; }
    public required PkgMetadata Metadata { get; init; }

    /// <summary>The decrypted item table. Empty when decryption was unavailable.</summary>
    public IReadOnlyList<PkgEntry> Entries { get; init; } = Array.Empty<PkgEntry>();

    /// <summary>Parsed PARAM.SFO, if the package contained one and it could be decrypted.</summary>
    public SfoTable? Sfo { get; init; }

    /// <summary>True when the item table (and thus entries/SFO) was decrypted.</summary>
    public bool IsDecrypted { get; init; }

    /// <summary>Why decryption was skipped, when <see cref="IsDecrypted"/> is false (e.g. missing key).</summary>
    public string? DecryptionNote { get; init; }

    public ContentId ContentId => Header.ContentId;

    public int FileCount => Entries.Count(e => e.IsFile);
    public int DirectoryCount => Entries.Count(e => e.IsDirectory);
}
