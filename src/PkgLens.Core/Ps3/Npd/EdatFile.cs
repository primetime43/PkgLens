using PkgLens.Core.Shared.Crypto;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PkgLens.Core.Ps3.Npd;

/// <summary>Parsed NPD/EDAT header fields (NPD 0x80 + EDAT 0x10).</summary>
public sealed class NpdInfo
{
    public int Version { get; init; }
    public int License { get; init; }
    public int Type { get; init; }
    public string ContentId { get; init; } = "";
    public byte[] Digest { get; init; } = new byte[16];
    public byte[] DevHash { get; init; } = new byte[16];
    public uint Flags { get; init; }
    public int BlockSize { get; init; }
    public long FileSize { get; init; }

    public bool IsSdat => (Flags & 0x01000000) != 0;
    public bool IsCompressed => (Flags & 0x00000001) != 0;
    public bool IsFree => (License & 0x3) == 0x3;

    /// <summary>True when this is an EDAT that needs a klicensee (from a RAP) — i.e. not SDAT and not free.</summary>
    public bool NeedsKlicensee => !IsSdat && !IsFree;

    public string LicenseText => License switch
    {
        1 => "Network", 2 => "Local", 3 => "Free", _ => $"0x{License:X}",
    };
}

/// <summary>
/// Decrypts PS3 NPDRM data files: <b>SDAT</b> (self-keyed, no license) and <b>EDAT</b> (needs the
/// content's klicensee, from a RAP or the free key). Algorithm ported from make_npdata (GPL).
/// Compressed EDATs are supported via <see cref="EdatLz"/>.
/// </summary>
public static class EdatFile
{
    private const uint Magic = 0x4E504400; // "NPD\0"
    private const int NpdSize = 0x80;
    private const int EdatSize = 0x10;
    private const int MetadataOffset = 0x100;
    private const uint PlainDataFlag = 0x00000002;
    private const uint EncryptedKeyFlag = 0x00000008;
    private const uint HmacFlag = 0x00000010;
    private const uint InterleavedMetadataFlag = 0x00000020;
    private const uint DebugDataFlag = 0x80000000;
    /// <summary>Upper bound on a sane EDAT/SDAT block size (real files use ~0x4000).</summary>
    private const int MaxBlockSize = 0x10000000; // 256 MiB

    /// <summary>True if the bytes begin with the NPD magic (an EDAT/SDAT file).</summary>
    public static bool IsEdat(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Magic;

    /// <summary>True if the stream begins with the NPD magic.</summary>
    public static bool IsNpd(Stream source)
    {
        if (!source.CanSeek || source.Length < 4) return false;
        long pos = source.Position;
        try
        {
            source.Position = 0;
            Span<byte> m = stackalloc byte[4];
            return source.Read(m) == 4 && BinaryPrimitives.ReadUInt32BigEndian(m) == Magic;
        }
        finally { source.Position = pos; }
    }

    public static NpdInfo ParseHeader(Stream source)
    {
        var head = ReadAt(source, 0, NpdSize + EdatSize);
        if (BinaryPrimitives.ReadUInt32BigEndian(head) != Magic)
        {
            // PSP EDATs ("\0PSPEDAT") share the .edat extension but are a different DRM format (PSP
            // AMCTRL/PGD, not PS3 NPDRM) that this decryptor does not handle.
            if (head[0] == 0x00 && Encoding.ASCII.GetString(head, 1, 7) == "PSPEDAT")
                throw new PkgFormatException(
                    "This is a PSP EDAT ('\\0PSPEDAT'), a different DRM format from the PS3 NPDRM EDAT/SDAT " +
                    "this tool decrypts. PSP EDAT decryption is not supported.");
            throw new PkgFormatException("Not an NPD/EDAT/SDAT file (bad magic).");
        }

        int blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x84));
        long fileSize = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(0x88));

        // block_size seeds the block-count division and per-block buffers; a zero (or absurd) value
        // in a malformed header would otherwise divide-by-zero or over-allocate. Real EDATs use
        // small powers of two (typically 0x4000). Reject anything outside a sane range up front.
        if (blockSize <= 0 || blockSize > MaxBlockSize)
            throw new PkgFormatException($"EDAT/SDAT block size 0x{blockSize:X} is invalid.");
        if (fileSize < 0)
            throw new PkgFormatException($"EDAT/SDAT file size 0x{fileSize:X} is invalid.");

        return new NpdInfo
        {
            Version = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x04)),
            License = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x08)),
            Type = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x0C)),
            ContentId = Encoding.ASCII.GetString(head, 0x10, 0x30).TrimEnd('\0'),
            Digest = head[0x40..0x50],
            DevHash = head[0x60..0x70],
            Flags = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x80)),
            BlockSize = blockSize,
            FileSize = fileSize,
        };
    }

    /// <summary>Decrypts the whole file to a byte array. <paramref name="klicensee"/> is required for licensed EDATs.</summary>
    public static byte[] DecryptToArray(Stream source, byte[]? klicensee = null)
    {
        using var ms = new MemoryStream();
        Decrypt(source, ms, klicensee);
        return ms.ToArray();
    }

    /// <summary>
    /// Verifies the authenticated header, metadata, and each encrypted block, then writes plaintext
    /// to <paramref name="destination"/>.
    /// </summary>
    public static void Decrypt(Stream source, Stream destination, byte[]? klicensee = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // PSP EDATs ("\0PSPEDAT") and bare PGDs ("\0PGD") are a different DRM format — route them to the
        // PSP decryptor (fixed-key AMCTRL/PGD, no RAP needed) rather than the PS3 NPDRM path below.
        if (Psp.PspEdatFile.IsPspEncrypted(source))
        {
            Psp.PspEdatFile.Decrypt(source, destination);
            return;
        }

        var npd = ParseHeader(source);

        // Select the crypt key.
        byte[] cryptKey;
        if (npd.IsSdat)
        {
            cryptKey = new byte[16];
            for (int i = 0; i < 16; i++) cryptKey[i] = (byte)(npd.DevHash[i] ^ NpdKeys.SdatKey[i]);
        }
        else if (npd.IsFree)
        {
            // Free-license EDATs can use a title-specific developer klicensee.
            cryptKey = klicensee ?? NpdKeys.KlicFree;
        }
        else
        {
            cryptKey = klicensee ?? throw new PkgKeyException(
                $"'{npd.ContentId}' is a licensed EDAT (DRM {npd.LicenseText}); a RAP/klicensee is required.");
            if (cryptKey.Length != 16)
                throw new PkgKeyException("Klicensee must be 16 bytes.");
        }

        byte[] edatKey = npd.Version == 4 ? NpdKeys.EdatKey1 : NpdKeys.EdatKey0;
        bool encryptedKey = (npd.Flags & EncryptedKeyFlag) != 0;
        bool debugData = (npd.Flags & DebugDataFlag) != 0;
        bool aesCbc = !debugData && (npd.Flags & PlainDataFlag) == 0;
        bool compressed = npd.IsCompressed;
        bool flag0x20 = (npd.Flags & InterleavedMetadataFlag) != 0;
        // COMPRESSED takes precedence over FLAG 0x20 for the metadata layout (matches RPCS3).
        int metadataEntry = (compressed || flag0x20) ? 0x20 : 0x10;

        long blockCount = npd.FileSize == 0 ? 0 : ((npd.FileSize - 1) / npd.BlockSize) + 1;
        if (blockCount > int.MaxValue)
            throw new PkgFormatException($"EDAT contains too many blocks ({blockCount:n0}).");
        int numBlocks = (int)blockCount;
        byte[] iv = npd.Version <= 1 ? new byte[16] : npd.Digest;

        using var aes = Aes.Create();
        aes.Padding = PaddingMode.None;

        if (!debugData)
        {
            byte[] headerHashKey = ResolveHashKey(aes, cryptKey, edatKey, encryptedKey);
            VerifyHeaderHash(source, npd, headerHashKey);
            VerifyMetadataHash(source, npd, headerHashKey, numBlocks, metadataEntry, flag0x20, cancellationToken);
        }

        for (int i = 0; i < numBlocks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long dataOffset;
            int readLen;    // bytes of ciphertext to read (16-aligned)
            int payloadLen; // meaningful decrypted bytes before optional decompression
            bool decompressBlock = false;
            byte[] metadata;

            if (compressed)
            {
                // Per-block metadata (0x20) is decrypted (unshuffled) into offset/length/compression_end.
                metadata = ReadAt(source, MetadataOffset + (long)i * 0x20, 0x20);
                (long off, int len, int compEnd) = npd.Version <= 1
                    ? (BinaryPrimitives.ReadInt64BigEndian(metadata.AsSpan(0x10)),
                       BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(0x18)),
                       BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(0x1C)))
                    : DecSection(metadata);
                if (len < 0 || len > npd.BlockSize || off < 0 || off > source.Length)
                    throw new PkgFormatException(
                        $"EDAT block {i} metadata is corrupt (offset 0x{off:X}, length 0x{len:X}).");
                dataOffset = off;
                payloadLen = len;
                readLen = (len + 15) & ~15;
                decompressBlock = compEnd != 0;
            }
            else
            {
                long metadataOffset = flag0x20
                    ? MetadataOffset + (long)i * (metadataEntry + npd.BlockSize)
                    : MetadataOffset + (long)i * metadataEntry;
                metadata = ReadAt(source, metadataOffset, metadataEntry);
                payloadLen = (int)Math.Min(npd.BlockSize, npd.FileSize - (long)i * npd.BlockSize);
                readLen = (payloadLen + 15) & ~15;
                dataOffset = flag0x20
                    ? MetadataOffset + (long)i * (0x20 + npd.BlockSize) + 0x20   // metadata+data interleaved
                    : MetadataOffset + (long)numBlocks * metadataEntry + (long)i * npd.BlockSize;
            }

            byte[] enc = ReadAt(source, dataOffset, readLen);

            // Per-block key: dev_hash[0..12] (zeros for v<=1) then the block index, big-endian.
            byte[] bKey = new byte[16];
            if (npd.Version > 1) Array.Copy(npd.DevHash, bKey, 12);
            BinaryPrimitives.WriteUInt32BigEndian(bKey.AsSpan(12), (uint)i);

            byte[] keyResult = EcbEncrypt(aes, cryptKey, bKey);
            byte[] dataKey = encryptedKey ? CbcDecrypt(aes, edatKey, new byte[16], keyResult) : keyResult;

            if (!debugData)
            {
                byte[] expectedHash = ExtractBlockHash(npd.Flags, metadata, compressed);
                byte[] hashSeed = (npd.Flags & HmacFlag) != 0
                    ? EcbEncrypt(aes, cryptKey, keyResult)
                    : keyResult;
                byte[] hashKey = ResolveHashKey(aes, hashSeed, edatKey, encryptedKey);
                byte[] actualHash = ComputeBlockHash(npd.Flags, hashKey, enc);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                    throw new PkgFormatException(
                        $"EDAT block {i} integrity check failed at offset 0x{dataOffset:X}; the encrypted data is corrupted.");
            }

            byte[] dec = aesCbc ? CbcDecrypt(aes, dataKey, iv, enc) : enc;

            if (decompressBlock)
            {
                var outBlock = new byte[npd.BlockSize];
                int res = EdatLz.Decompress(outBlock, dec, npd.BlockSize);
                if (res < 0)
                    throw new PkgFormatException($"Failed to decompress EDAT block {i} (offset 0x{dataOffset:X}).");
                destination.Write(outBlock, 0, res);
            }
            else
            {
                destination.Write(dec, 0, payloadLen);
            }
        }
    }

    private static void VerifyHeaderHash(Stream source, NpdInfo npd, byte[] hashKey)
    {
        byte[] header = ReadAt(source, 0, 0xA0);
        byte[] expected = ReadAt(source, 0xA0, 0x10);
        byte[] actual = AesCmac.Compute(hashKey, header);
        if (CryptographicOperations.FixedTimeEquals(actual, expected)) return;

        if (npd.NeedsKlicensee)
            throw new PkgKeyException(
                $"EDAT header integrity check failed for '{npd.ContentId}'; the RAP/klicensee is wrong.");

        throw new PkgFormatException("EDAT header integrity check failed; the file header is corrupted.");
    }

    private static void VerifyMetadataHash(
        Stream source,
        NpdInfo npd,
        byte[] hashKey,
        int numBlocks,
        int metadataEntry,
        bool flag0x20,
        CancellationToken cancellationToken)
    {
        using var cmac = new AesCmacAccumulator(hashKey);
        for (int i = 0; i < numBlocks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = flag0x20
                ? MetadataOffset + (long)i * (metadataEntry + npd.BlockSize)
                : MetadataOffset + (long)i * metadataEntry;
            cmac.Append(ReadAt(source, offset, metadataEntry));
        }

        byte[] actual = cmac.FinalizeHash();
        byte[] expected = ReadAt(source, 0x90, 0x10);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new PkgFormatException("EDAT metadata integrity check failed; the file metadata is corrupted.");
    }

    private static byte[] ResolveHashKey(Aes aes, byte[] hash, byte[] edatKey, bool encryptedKey) =>
        encryptedKey ? CbcDecrypt(aes, edatKey, new byte[16], hash) : hash;

    private static byte[] ExtractBlockHash(uint flags, byte[] metadata, bool compressed)
    {
        bool flag0x20 = (flags & InterleavedMetadataFlag) != 0;
        int hashLength = flag0x20 && (flags & HmacFlag) != 0 ? 20 : 16;
        var hash = new byte[hashLength];
        metadata.AsSpan(0, Math.Min(16, hashLength)).CopyTo(hash);
        if (!flag0x20 || compressed) return hash;

        metadata.AsSpan(0, hashLength).CopyTo(hash);
        for (int i = 0; i < 16; i++) hash[i] ^= metadata[i + 16];
        return hash;
    }

    private static byte[] ComputeBlockHash(uint flags, byte[] hashKey, byte[] encryptedData)
    {
        if ((flags & HmacFlag) == 0)
            return AesCmac.Compute(hashKey, encryptedData);

        byte[] hash = HMACSHA1.HashData(hashKey, encryptedData);
        return (flags & InterleavedMetadataFlag) != 0 ? hash : hash[..16];
    }

    /// <summary>
    /// Unshuffles a compressed EDAT's per-block metadata (0x20 bytes) into the block's data offset,
    /// length and compression flag. The fixed XOR permutation is from RPCS3's <c>dec_section</c>.
    /// </summary>
    private static (long offset, int length, int compressionEnd) DecSection(byte[] m)
    {
        Span<byte> d = stackalloc byte[0x10];
        d[0x00] = (byte)(m[0xC] ^ m[0x8] ^ m[0x10]);
        d[0x01] = (byte)(m[0xD] ^ m[0x9] ^ m[0x11]);
        d[0x02] = (byte)(m[0xE] ^ m[0xA] ^ m[0x12]);
        d[0x03] = (byte)(m[0xF] ^ m[0xB] ^ m[0x13]);
        d[0x04] = (byte)(m[0x4] ^ m[0x8] ^ m[0x14]);
        d[0x05] = (byte)(m[0x5] ^ m[0x9] ^ m[0x15]);
        d[0x06] = (byte)(m[0x6] ^ m[0xA] ^ m[0x16]);
        d[0x07] = (byte)(m[0x7] ^ m[0xB] ^ m[0x17]);
        d[0x08] = (byte)(m[0xC] ^ m[0x0] ^ m[0x18]);
        d[0x09] = (byte)(m[0xD] ^ m[0x1] ^ m[0x19]);
        d[0x0A] = (byte)(m[0xE] ^ m[0x2] ^ m[0x1A]);
        d[0x0B] = (byte)(m[0xF] ^ m[0x3] ^ m[0x1B]);
        d[0x0C] = (byte)(m[0x4] ^ m[0x0] ^ m[0x1C]);
        d[0x0D] = (byte)(m[0x5] ^ m[0x1] ^ m[0x1D]);
        d[0x0E] = (byte)(m[0x6] ^ m[0x2] ^ m[0x1E]);
        d[0x0F] = (byte)(m[0x7] ^ m[0x3] ^ m[0x1F]);

        long offset = BinaryPrimitives.ReadInt64BigEndian(d);
        int length = BinaryPrimitives.ReadInt32BigEndian(d[0x08..]);
        int compressionEnd = BinaryPrimitives.ReadInt32BigEndian(d[0x0C..]);
        return (offset, length, compressionEnd);
    }

    private static byte[] EcbEncrypt(Aes aes, byte[] key, byte[] data)
    {
        aes.Mode = CipherMode.ECB;
        aes.Key = key;
        return aes.EncryptEcb(data, PaddingMode.None);
    }

    private static byte[] CbcDecrypt(Aes aes, byte[] key, byte[] iv, byte[] data)
    {
        aes.Mode = CipherMode.CBC;
        aes.Key = key;
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] ReadAt(Stream stream, long offset, int length)
    {
        if (offset < 0 || offset + length > stream.Length)
            throw new PkgFormatException($"Truncated EDAT: read [0x{offset:X}, +0x{length:X}) exceeds file length.");
        stream.Position = offset;
        var buf = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(buf, read, length - read);
            if (n <= 0) throw new PkgFormatException("Unexpected end of EDAT while reading data.");
            read += n;
        }
        return buf;
    }
}
