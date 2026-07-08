using System.Buffers.Binary;
using System.Text;

namespace PkgLens.Core.Models;

/// <summary>
/// The PS3 PKG main header. All multi-byte values are big-endian. Layout verified against
/// RPCS3's <c>unpkg.h</c> PKGHeader struct:
/// <code>
/// 0x00 u32  magic          = 0x7F504B47 (".PKG"), stored big-endian
/// 0x04 u16  finalization   0x8000 retail, 0x0000 debug
/// 0x06 u16  platform       0x0001 PS3, 0x0002 PSP/PSVita
/// 0x08 u32  metadata_offset
/// 0x0C u32  metadata_count
/// 0x10 u32  metadata_size
/// 0x14 u32  item_count
/// 0x18 u64  total_size
/// 0x20 u64  data_offset
/// 0x28 u64  data_size
/// 0x30 char[0x24] content_id  (36 ASCII bytes, within a 0x30-byte field)
/// 0x60 u8[0x10]   qa_digest   (seeds the debug SHA-1 keystream)
/// 0x70 u8[0x10]   data_riv    (klicensee — AES-CTR counter seed for retail)
/// 0x80 u8[0x40]   header CMAC (0x10) + ECDSA signature (0x28) + padding
/// </code>
/// </summary>
public sealed class PkgHeader
{
    /// <summary>Expected value at offset 0x00: bytes 7F 'P' 'K' 'G'.</summary>
    public const uint Magic = 0x7F504B47;

    /// <summary>Number of header bytes we require before attempting a parse (through data_riv).</summary>
    public const int MinLength = 0x80;

    /// <summary>Offset at which the (encrypted) item table + file data begin.</summary>
    public const int ContentIdLength = 0x24;

    public uint RawMagic { get; init; }
    public ushort RawFinalization { get; init; }
    public ushort RawPlatform { get; init; }

    public PkgFinalization Finalization { get; init; }
    public PkgPlatform Platform { get; init; }

    public uint MetadataOffset { get; init; }
    public uint MetadataCount { get; init; }
    public uint MetadataSize { get; init; }
    public uint ItemCount { get; init; }
    public ulong TotalSize { get; init; }
    public ulong DataOffset { get; init; }
    public ulong DataSize { get; init; }

    public ContentId ContentId { get; init; } = new(string.Empty, null, null, null, null);

    /// <summary>16-byte QA digest at 0x60. Seeds the debug keystream.</summary>
    public byte[] QaDigest { get; init; } = new byte[0x10];

    /// <summary>16-byte data RIV / klicensee at 0x70. Retail AES-CTR counter seed.</summary>
    public byte[] DataRiv { get; init; } = new byte[0x10];

    public bool IsPs3 => Platform == PkgPlatform.Ps3;
    public bool IsRetail => Finalization == PkgFinalization.Retail;

    /// <summary>
    /// Parses a header from at least <see cref="MinLength"/> bytes. Throws
    /// <see cref="PkgFormatException"/> (never a raw index/format exception) on bad magic or
    /// a buffer that is too short.
    /// </summary>
    public static PkgHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinLength)
            throw new PkgFormatException(
                $"PKG header truncated: need at least 0x{MinLength:X} bytes, got 0x{data.Length:X}.");

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data[0x00..]);
        if (magic != Magic)
            throw new PkgFormatException(
                $"Not a PS3 PKG: magic 0x{magic:X8} does not match 0x{Magic:X8}.");

        ushort rawFin = BinaryPrimitives.ReadUInt16BigEndian(data[0x04..]);
        ushort rawPlat = BinaryPrimitives.ReadUInt16BigEndian(data[0x06..]);

        var contentIdBytes = data.Slice(0x30, ContentIdLength);
        string contentIdRaw = Encoding.ASCII.GetString(contentIdBytes).TrimEnd('\0');

        return new PkgHeader
        {
            RawMagic = magic,
            RawFinalization = rawFin,
            RawPlatform = rawPlat,
            Finalization = rawFin switch
            {
                0x8000 => PkgFinalization.Retail,
                0x0000 => PkgFinalization.Debug,
                _ => PkgFinalization.Unknown,
            },
            Platform = rawPlat switch
            {
                0x0001 => PkgPlatform.Ps3,
                0x0002 => PkgPlatform.PspPsVita,
                _ => PkgPlatform.Unknown,
            },
            MetadataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[0x08..]),
            MetadataCount = BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]),
            MetadataSize = BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]),
            ItemCount = BinaryPrimitives.ReadUInt32BigEndian(data[0x14..]),
            TotalSize = BinaryPrimitives.ReadUInt64BigEndian(data[0x18..]),
            DataOffset = BinaryPrimitives.ReadUInt64BigEndian(data[0x20..]),
            DataSize = BinaryPrimitives.ReadUInt64BigEndian(data[0x28..]),
            ContentId = ContentId.Parse(contentIdRaw),
            QaDigest = data.Slice(0x60, 0x10).ToArray(),
            DataRiv = data.Slice(0x70, 0x10).ToArray(),
        };
    }
}
