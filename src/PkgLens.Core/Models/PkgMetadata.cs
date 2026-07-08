using System.Buffers.Binary;

namespace PkgLens.Core.Models;

/// <summary>
/// Well-known metadata entry ids. The metadata block is a flat sequence of records, each
/// <c>u32 id, u32 size, size bytes</c>. Unknown ids are preserved as raw bytes rather than
/// dropped. Ids mirror the PS3 PKG metadata table documented by psdevwiki / RPCS3.
/// </summary>
public enum PkgMetadataId : uint
{
    DrmType = 0x01,
    ContentType = 0x02,
    PackageFlags = 0x03,
    PackageSize = 0x04,
    MakePackageNpdrmRevision = 0x05,
    TitleId = 0x06,
    QaDigest = 0x07,
    SystemVersion = 0x08,
    InstallDirectory = 0x0A,
    PsVitaItemInfo = 0x0E,
    PsVitaSfoInfo = 0x0F,
    PsVitaUnknownDataInfo = 0x10,
    PsVitaEntiretyInfo = 0x11,
    PsVitaVersionInfo = 0x12,
    PsVitaSelfInfo = 0x13,
}

/// <summary>Common values of the <see cref="PkgMetadataId.ContentType"/> entry.</summary>
public enum PkgContentType : uint
{
    GameData = 0x4,
    GameExec = 0x5,
    Ps1Emu = 0x6,
    Psp = 0x7,
    Theme = 0x9,
    Widget = 0xA,
    License = 0xB,
    Vsh = 0xC,
    VshAvc = 0xD,
    Psp2Gd = 0xE,
    Psp2Ac = 0xF,
    Psp2LiveArea = 0x10,
    PspGo = 0x11,
    MiniS = 0x12,
    NeoGeo = 0x13,
    Vmc = 0x14,
    Ps2Classic = 0x1B,
}

/// <summary>One raw metadata record. <see cref="Data"/> is the payload after the id/size header.</summary>
public sealed class PkgMetadataEntry
{
    public uint RawId { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <summary>The typed id if recognized, otherwise null (see <see cref="RawId"/>).</summary>
    public PkgMetadataId? Id =>
        Enum.IsDefined(typeof(PkgMetadataId), RawId) ? (PkgMetadataId)RawId : null;

    /// <summary>Interprets the first 4 payload bytes as a big-endian u32, if present.</summary>
    public uint? AsUInt32() =>
        Data.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(Data) : null;

    /// <summary>Lower-case hex rendering of the raw payload, for display of unknown entries.</summary>
    public string ToHex() => Convert.ToHexString(Data).ToLowerInvariant();

    public string Label => Id?.ToString() ?? $"0x{RawId:X2}";
}

/// <summary>The parsed metadata block: an ordered list of records plus typed convenience accessors.</summary>
public sealed class PkgMetadata
{
    public IReadOnlyList<PkgMetadataEntry> Entries { get; }

    public PkgMetadata(IReadOnlyList<PkgMetadataEntry> entries) => Entries = entries;

    public PkgMetadataEntry? Find(PkgMetadataId id) =>
        Entries.FirstOrDefault(e => e.RawId == (uint)id);

    public uint? DrmType => Find(PkgMetadataId.DrmType)?.AsUInt32();

    public uint? ContentTypeRaw => Find(PkgMetadataId.ContentType)?.AsUInt32();

    public PkgContentType? ContentType =>
        ContentTypeRaw is uint v && Enum.IsDefined(typeof(PkgContentType), v)
            ? (PkgContentType)v
            : null;

    /// <summary>The install directory string (0x0A), if present.</summary>
    public string? InstallDirectory
    {
        get
        {
            var e = Find(PkgMetadataId.InstallDirectory);
            return e is null ? null : System.Text.Encoding.ASCII.GetString(e.Data).TrimEnd('\0');
        }
    }

    /// <summary>
    /// Parses the metadata block. <paramref name="count"/> records are read from the start of
    /// <paramref name="block"/>; a record whose declared size overruns the block is a format error.
    /// </summary>
    public static PkgMetadata Parse(ReadOnlySpan<byte> block, uint count)
    {
        var entries = new List<PkgMetadataEntry>((int)Math.Min(count, 256));
        int pos = 0;
        for (uint i = 0; i < count; i++)
        {
            if (pos + 8 > block.Length)
                throw new PkgFormatException(
                    $"Metadata entry {i} header overruns block (pos 0x{pos:X}, len 0x{block.Length:X}).");

            uint id = BinaryPrimitives.ReadUInt32BigEndian(block[pos..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(block[(pos + 4)..]);
            pos += 8;

            if (pos + (long)size > block.Length)
                throw new PkgFormatException(
                    $"Metadata entry {i} (id 0x{id:X}) size 0x{size:X} overruns block.");

            entries.Add(new PkgMetadataEntry
            {
                RawId = id,
                Data = block.Slice(pos, (int)size).ToArray(),
            });
            pos += (int)size;
        }

        return new PkgMetadata(entries);
    }
}
