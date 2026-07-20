using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PkgLens.Core.Ps3.Trophy;

public enum TrophyGrade
{
    Unknown,
    Bronze,
    Silver,
    Gold,
    Platinum,
}

public sealed record TrophyItem(
    int Id,
    string Name,
    string Description,
    TrophyGrade Grade,
    bool IsHidden,
    byte[]? Icon);

public sealed class TrophySet
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<TrophyItem> Trophies { get; init; }
    public required string MetadataFileName { get; init; }
    public required uint ArchiveVersion { get; init; }
    public required bool ChecksumVerified { get; init; }
    public byte[]? Artwork { get; init; }

    public int BronzeCount => Trophies.Count(trophy => trophy.Grade == TrophyGrade.Bronze);
    public int SilverCount => Trophies.Count(trophy => trophy.Grade == TrophyGrade.Silver);
    public int GoldCount => Trophies.Count(trophy => trophy.Grade == TrophyGrade.Gold);
    public int PlatinumCount => Trophies.Count(trophy => trophy.Grade == TrophyGrade.Platinum);
}

/// <summary>
/// Reads a PS3 <c>TROPHY.TRP</c> archive and its localized trophy configuration. The archive layout
/// follows Sony's TRP container as implemented by RPCS3: a 0x40-byte big-endian header followed by
/// fixed-size entry records. Version 2+ archives carry a SHA-1 over the declared file size with the
/// header's digest field zeroed.
/// </summary>
public static class TrophyArchive
{
    public const uint Magic = 0xDCA24D00;
    public const int HeaderSize = 0x40;
    public const int EntrySize = 0x40;

    private const int Sha1Offset = 0x1c;
    private const int Sha1Length = 20;
    private const int MaxEntries = 10_000;
    private const int MaxMetadataBytes = 16 * 1024 * 1024;

    private sealed record ArchiveEntry(string Name, int Offset, int Size);

    public static bool IsTrophyArchive(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && BinaryPrimitives.ReadUInt32BigEndian(data) == Magic;

    public static TrophySet Read(byte[] data, string? trophySetId = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsTrophyArchive(data))
            throw new PkgFormatException("Not a TROPHY.TRP archive (unexpected magic).");

        ReadOnlySpan<byte> header = data.AsSpan(0, HeaderSize);
        uint version = BinaryPrimitives.ReadUInt32BigEndian(header[0x04..]);
        ulong declaredSize = BinaryPrimitives.ReadUInt64BigEndian(header[0x08..]);
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(header[0x10..]);
        uint entrySize = BinaryPrimitives.ReadUInt32BigEndian(header[0x14..]);

        if (declaredSize < HeaderSize || declaredSize > (ulong)data.Length || declaredSize > int.MaxValue)
            throw new PkgFormatException($"TROPHY.TRP declares an invalid file size of {declaredSize:n0} bytes.");
        if (entryCount > MaxEntries)
            throw new PkgFormatException($"TROPHY.TRP declares too many entries ({entryCount:n0}).");
        if (entrySize < EntrySize || entrySize > 0x1000)
            throw new PkgFormatException($"TROPHY.TRP has an unsupported entry size of 0x{entrySize:X}.");

        ulong tableEnd = HeaderSize + (ulong)entryCount * entrySize;
        if (tableEnd > declaredSize)
            throw new PkgFormatException("TROPHY.TRP entry table extends beyond the declared file size.");

        bool checksumVerified = false;
        if (version >= 2)
        {
            int fileSize = checked((int)declaredSize);
            byte[] authenticated = data.AsSpan(0, fileSize).ToArray();
            authenticated.AsSpan(Sha1Offset, Sha1Length).Clear();
            Span<byte> computed = stackalloc byte[Sha1Length];
            SHA1.HashData(authenticated, computed);
            if (!CryptographicOperations.FixedTimeEquals(computed, header.Slice(Sha1Offset, Sha1Length)))
                throw new PkgFormatException("TROPHY.TRP SHA-1 checksum does not match; the archive is corrupt or modified.");
            checksumVerified = true;
        }

        var entries = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; index < entryCount; index++)
        {
            int recordOffset = checked(HeaderSize + (int)(index * entrySize));
            ReadOnlySpan<byte> record = data.AsSpan(recordOffset, EntrySize);
            string name = ReadName(record[..0x20]);
            if (name.Length == 0)
                throw new PkgFormatException($"TROPHY.TRP entry {index} has no filename.");

            ulong offset = BinaryPrimitives.ReadUInt64BigEndian(record[0x20..]);
            ulong size = BinaryPrimitives.ReadUInt64BigEndian(record[0x28..]);
            if (offset > declaredSize || size > declaredSize - offset ||
                offset > int.MaxValue || size > int.MaxValue)
                throw new PkgFormatException($"TROPHY.TRP entry '{name}' is outside the archive bounds.");
            if (!entries.TryAdd(name, new ArchiveEntry(name, (int)offset, (int)size)))
                throw new PkgFormatException($"TROPHY.TRP contains duplicate entry '{name}'.");
        }

        ArchiveEntry metadata = FindMetadata(entries) ??
            throw new PkgFormatException("TROPHY.TRP contains no TROPCONF.SFM, TROP.SFM, or localized trophy configuration.");
        if (metadata.Size > MaxMetadataBytes)
            throw new PkgFormatException($"Trophy metadata '{metadata.Name}' is implausibly large ({metadata.Size:n0} bytes).");

        XDocument document = ReadMetadata(data.AsSpan(metadata.Offset, metadata.Size), metadata.Name);
        XElement root = document.Root is { } documentRoot && IsName(documentRoot, "trophyconf")
            ? documentRoot
            : document.Descendants().FirstOrDefault(element => IsName(element, "trophyconf"))
              ?? throw new PkgFormatException($"{metadata.Name} has no trophyconf element.");

        string resolvedId = FirstValue(root, "npcommid", "npcommunicationid")
            ?? root.Attribute("npcommid")?.Value
            ?? trophySetId
            ?? "Unknown";
        resolvedId = resolvedId.Trim();

        string? setName = DirectValue(root, "title-name")?.Trim();
        if (string.IsNullOrWhiteSpace(setName))
            setName = resolvedId == "Unknown" ? "Trophy set" : resolvedId;
        string description = DirectValue(root, "title-detail")?.Trim() ?? string.Empty;

        var trophies = new List<TrophyItem>();
        var ids = new HashSet<int>();
        foreach (XElement element in root.Elements().Where(child => IsName(child, "trophy")))
        {
            string? idText = element.Attribute("id")?.Value;
            if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 0)
                throw new PkgFormatException($"{metadata.Name} contains a trophy with invalid id '{idText}'.");
            if (!ids.Add(id))
                throw new PkgFormatException($"{metadata.Name} contains duplicate trophy id {id}.");

            TrophyGrade grade = ParseGrade(element.Attribute("ttype")?.Value);
            bool hidden = ParseHidden(element.Attribute("hidden")?.Value);
            string? name = DirectValue(element, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = $"Trophy {id}";
            string detail = DirectValue(element, "detail")?.Trim() ?? string.Empty;
            byte[]? icon = ReadOptional(entries, data, $"TROP{id:D3}.PNG");
            trophies.Add(new TrophyItem(id, name, detail, grade, hidden, icon));
        }

        trophies.Sort((left, right) => left.Id.CompareTo(right.Id));
        return new TrophySet
        {
            Id = resolvedId,
            Name = setName,
            Description = description,
            Trophies = trophies,
            MetadataFileName = metadata.Name,
            ArchiveVersion = version,
            ChecksumVerified = checksumVerified,
            Artwork = ReadOptional(entries, data, "ICON0.PNG"),
        };
    }

    private static ArchiveEntry? FindMetadata(IReadOnlyDictionary<string, ArchiveEntry> entries)
    {
        // TROPCONF.SFM inside a TRP is commonly the reduced installer configuration: it has IDs,
        // grades, and hidden flags but no user-facing text. TROP.SFM is the default English file,
        // while TROP_01.SFM is the explicit English localization. Prefer either rich text source
        // before falling back to the reduced configuration.
        foreach (string candidate in new[] { "TROP_01.SFM", "TROP.SFM", "TROPCONF.SFM" })
            if (entries.TryGetValue(candidate, out ArchiveEntry? entry))
                return entry;
        return entries.Values
            .Where(entry => entry.Name.StartsWith("TROP_", StringComparison.OrdinalIgnoreCase) &&
                            entry.Name.EndsWith(".SFM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static XDocument ReadMetadata(ReadOnlySpan<byte> data, string name)
    {
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxMetadataBytes,
            };
            using XmlReader reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new PkgFormatException($"Trophy metadata '{name}' is not valid XML: {exception.Message}");
        }
    }

    private static byte[]? ReadOptional(IReadOnlyDictionary<string, ArchiveEntry> entries,
        byte[] data, string name) =>
        entries.TryGetValue(name, out ArchiveEntry? entry)
            ? data.AsSpan(entry.Offset, entry.Size).ToArray()
            : null;

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        int terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0)
            bytes = bytes[..terminator];
        return Encoding.ASCII.GetString(bytes);
    }

    private static TrophyGrade ParseGrade(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "B" or "BRONZE" => TrophyGrade.Bronze,
        "S" or "SILVER" => TrophyGrade.Silver,
        "G" or "GOLD" => TrophyGrade.Gold,
        "P" or "PLATINUM" => TrophyGrade.Platinum,
        _ => TrophyGrade.Unknown,
    };

    private static bool ParseHidden(string? value) => value?.Trim().ToLowerInvariant() is
        "yes" or "true" or "1";

    private static bool IsName(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string? DirectValue(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => IsName(child, name))?.Value;

    private static string? FirstValue(XElement element, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = element.Descendants().FirstOrDefault(child => IsName(child, name))?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }
}
