using System.Buffers.Binary;

namespace PkgLens.Core.Models;

/// <summary>
/// One record from the (decrypted) item table. Each record is 0x20 bytes:
/// <code>
/// 0x00 u32 name_offset   (relative to header.data_offset)
/// 0x04 u32 name_size
/// 0x08 u64 file_offset   (relative to header.data_offset)
/// 0x10 u64 file_size
/// 0x18 u32 type          (low byte = kind, high bits = flags)
/// 0x1C u32 padding
/// </code>
/// The name string itself lives in the encrypted data region at <see cref="NameOffset"/> and
/// is resolved by the reader.
/// </summary>
public sealed class PkgEntry
{
    /// <summary>Size of one packed item record on disk.</summary>
    public const int RecordSize = 0x20;

    public const uint FlagOverwrite = 0x80000000;
    public const uint FlagPsp = 0x10000000;

    public uint NameOffset { get; init; }
    public uint NameSize { get; init; }
    public ulong FileOffset { get; init; }
    public ulong FileSize { get; init; }
    public uint RawType { get; init; }

    /// <summary>Resolved (decrypted) entry name, relative path within the package.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The low-byte kind of the entry.</summary>
    public PkgEntryType Kind => (PkgEntryType)(RawType & 0xFF);

    public bool IsDirectory => Kind == PkgEntryType.Folder;
    public bool IsFile => !IsDirectory;

    /// <summary>
    /// PS3 PKG file data is always stored inside the encrypted data region, so every real file
    /// entry is encrypted. Directories carry no data.
    /// </summary>
    public bool IsEncrypted => IsFile && FileSize > 0;

    /// <summary>Parses a single item record from exactly (at least) 0x20 bytes.</summary>
    public static PkgEntry ParseRecord(ReadOnlySpan<byte> data)
    {
        if (data.Length < RecordSize)
            throw new PkgFormatException(
                $"Item record truncated: need 0x{RecordSize:X} bytes, got 0x{data.Length:X}.");

        return new PkgEntry
        {
            NameOffset = BinaryPrimitives.ReadUInt32BigEndian(data[0x00..]),
            NameSize = BinaryPrimitives.ReadUInt32BigEndian(data[0x04..]),
            FileOffset = BinaryPrimitives.ReadUInt64BigEndian(data[0x08..]),
            FileSize = BinaryPrimitives.ReadUInt64BigEndian(data[0x10..]),
            RawType = BinaryPrimitives.ReadUInt32BigEndian(data[0x18..]),
        };
    }
}
