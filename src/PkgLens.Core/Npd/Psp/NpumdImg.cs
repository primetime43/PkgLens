using System.Buffers.Binary;
using PkgLens.Core.Npd;

namespace PkgLens.Core.Npd.Psp;

/// <summary>Decoded metadata from an NPUMDIMG header (after the encrypted body is opened).</summary>
public sealed class NpumdImgInfo
{
    public required string ContentId { get; init; }
    public required uint NpFlags { get; init; }
    public required int BlockBasis { get; init; }
    public required int SectorSize { get; init; }
    public required long TotalSectors { get; init; }
    public required int BlockCount { get; init; }
    public required string DiscId { get; init; }

    /// <summary>Whether the header body decrypted to sane values (the klicensee is correct).</summary>
    public bool HeaderValid => SectorSize == 0x800 && BlockEntryOffset == 0x100;

    /// <summary>Offset of the block table (always 0x100 for a valid image).</summary>
    public required uint BlockEntryOffset { get; init; }

    /// <summary>Uncompressed bytes per full block (block_basis × sector_size).</summary>
    public long BlockSize => (long)BlockBasis * SectorSize;

    /// <summary>Padded ISO size (block_count × block_size); the mountable image is this size.</summary>
    public long IsoSize => (long)BlockCount * BlockSize;

    /// <summary>True when the version key is the klicensee (retail / RAP-licensed content).</summary>
    public bool NeedsKlicensee => (NpFlags & 2) != 0;
}

/// <summary>
/// Decrypts a PSP NPUMDIMG image (the <c>DATA.PSAR</c> inside a minis / PSP-remaster <c>EBOOT.PBP</c>)
/// back to a plain PSP <c>.iso</c>. The version key is the klicensee (from the package's RAP) for
/// RAP-licensed content. The header body and each data block are AMCTRL BBCipher-encrypted with the
/// (plaintext) header key + version key; blocks may be LZRC-compressed (the same range coder as EDAT,
/// <see cref="EdatLz"/>). The block table is a keyless XOR scramble. Faithful inverse of hykem's
/// sign_np; all keys public, nothing is re-signed.
/// </summary>
public static class NpumdImg
{
    private static readonly byte[] Magic = "NPUMDIMG"u8.ToArray();
    private const int HeaderSize = 0x100;
    private const int TableEntrySize = 0x20;

    public static bool IsNpumdImg(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(Magic);

    /// <summary>Parses and opens the header, returning the disc metadata. Requires the klicensee for RAP content.</summary>
    public static NpumdImgInfo ParseHeader(ReadOnlySpan<byte> header, byte[]? klicensee)
    {
        if (header.Length < HeaderSize || !IsNpumdImg(header))
            throw new PkgFormatException("Not an NPUMDIMG image (bad magic or short header).");

        // NPUMDIMG is a little-endian PSP structure (unlike the big-endian PS3 PKG).
        uint npFlags = BinaryPrimitives.ReadUInt32LittleEndian(header[0x08..]);
        int blockBasis = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[0x0C..]);
        string contentId = System.Text.Encoding.ASCII.GetString(header.Slice(0x10, 0x30)).TrimEnd('\0');

        byte[] versionKey = ResolveVersionKey(npFlags, klicensee);
        byte[] headerKey = header.Slice(0xA0, 0x10).ToArray();

        // Decrypt the 0x60-byte body at 0x40 (BBCipher, seed 0) to reveal the disc layout.
        var body = header.Slice(0x40, 0x60).ToArray();
        var amctrl = new PspAmctrl();
        var ckey = new CipherKey();
        amctrl.BBCipherInit(ckey, 1, headerKey, 0, versionKey, 0);
        amctrl.BBCipherUpdate(ckey, body, 0, body.Length);

        int sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0x00));
        long lbaEnd = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0x24));
        uint blockEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0x2C));
        string discId = System.Text.Encoding.ASCII.GetString(body.AsSpan(0x30, 0x10)).TrimEnd('\0', ' ');

        long totalSectors = lbaEnd + 1;
        int blockCount = blockBasis > 0 ? (int)((totalSectors + blockBasis - 1) / blockBasis) : 0;

        return new NpumdImgInfo
        {
            ContentId = contentId,
            NpFlags = npFlags,
            BlockBasis = blockBasis,
            SectorSize = sectorSize,
            TotalSectors = totalSectors,
            BlockCount = blockCount,
            DiscId = discId,
            BlockEntryOffset = blockEntryOffset,
        };
    }

    /// <summary>
    /// Decrypts the whole NPUMDIMG in <paramref name="source"/> to a PSP ISO on
    /// <paramref name="destination"/>. <paramref name="klicensee"/> is the 16-byte key from the RAP
    /// (required for RAP-licensed images). Streams block-by-block; the source must be seekable.
    /// </summary>
    public static void DecryptToIso(Stream source, Stream destination, byte[]? klicensee)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var header = new byte[HeaderSize];
        source.Position = 0;
        source.ReadExactly(header, 0, HeaderSize);
        var info = ParseHeader(header, klicensee);
        if (!info.HeaderValid)
            throw new PkgKeyException("NPUMDIMG header did not decrypt to a valid layout — wrong klicensee/RAP.");

        byte[] versionKey = ResolveVersionKey(info.NpFlags, klicensee);
        byte[] headerKey = header.AsSpan(0xA0, 0x10).ToArray();

        // Read and XOR-descramble the block table.
        long tableSize = (long)info.BlockCount * TableEntrySize;
        var table = new byte[tableSize];
        source.Position = info.BlockEntryOffset;
        source.ReadExactly(table, 0, table.Length);

        var amctrl = new PspAmctrl();
        int blockSize = (int)info.BlockSize;
        var blockBuf = new byte[blockSize + 0x10];
        var outBuf = new byte[blockSize];

        for (int i = 0; i < info.BlockCount; i++)
        {
            var entry = table.AsSpan(i * TableEntrySize, TableEntrySize);
            DescrambleTableEntry(entry);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x10..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x14..]);

            int aligned = (int)((size + 0x0F) & ~0x0Fu);
            if (aligned > blockBuf.Length) blockBuf = new byte[aligned];
            source.Position = offset;
            source.ReadExactly(blockBuf, 0, aligned);

            // Decrypt the block with the per-block seed (offset >> 4).
            var ckey = new CipherKey();
            amctrl.BBCipherInit(ckey, 1, headerKey, 0, versionKey, offset >> 4);
            amctrl.BBCipherUpdate(ckey, blockBuf, 0, aligned);

            int outLen;
            if (size >= blockSize)
            {
                // Stored uncompressed (a full block).
                outLen = blockSize;
                Array.Copy(blockBuf, outBuf, blockSize);
            }
            else
            {
                // Smaller than a block → LZRC-compressed (fall back to raw if it isn't).
                int n = EdatLz.Decompress(outBuf, blockBuf, (int)size);
                if (n > 0)
                {
                    outLen = n;
                }
                else
                {
                    outLen = (int)size;
                    Array.Copy(blockBuf, outBuf, outLen);
                }
            }
            destination.Write(outBuf, 0, outLen);
        }
    }

    /// <summary>The block table entry XOR scramble is self-inverse (the MAC half keys the data half).</summary>
    private static void DescrambleTableEntry(Span<byte> entry)
    {
        uint p0 = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x00..]);
        uint p1 = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x04..]);
        uint p2 = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x08..]);
        uint p3 = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x0C..]);
        uint k0 = p0 ^ p1, k1 = p1 ^ p2, k2 = p0 ^ p3, k3 = p2 ^ p3;

        WriteXor(entry[0x10..], k3);
        WriteXor(entry[0x14..], k1);
        WriteXor(entry[0x18..], k2);
        WriteXor(entry[0x1C..], k0);
    }

    private static void WriteXor(Span<byte> at, uint key)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(at) ^ key;
        BinaryPrimitives.WriteUInt32LittleEndian(at, v);
    }

    private static byte[] ResolveVersionKey(uint npFlags, byte[]? klicensee)
    {
        if ((npFlags & 2) != 0)
        {
            if (klicensee is not { Length: 16 })
                throw new PkgKeyException(
                    "This NPUMDIMG is RAP-licensed — supply the 16-byte klicensee (from the package's RAP).");
            return klicensee;
        }
        throw new PkgKeyException(
            "This NPUMDIMG uses a content-derived fixed key (sceNpDrmGetFixedKey), which is not yet supported.");
    }
}
