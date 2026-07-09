using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PkgLens.Core.Npd;

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
/// content's klicensee, from a RAP or the free key). Algorithm ported from make_npdata (GPL) and
/// verified byte-for-byte against a real retail EDAT. Compressed EDATs are detected but not yet
/// decompressed.
/// </summary>
public static class EdatFile
{
    private const uint Magic = 0x4E504400; // "NPD\0"
    private const int NpdSize = 0x80;
    private const int EdatSize = 0x10;
    private const int MetadataOffset = 0x100;

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
            throw new PkgFormatException("Not an NPD/EDAT/SDAT file (bad magic).");

        return new NpdInfo
        {
            Version = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x04)),
            License = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x08)),
            Type = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x0C)),
            ContentId = Encoding.ASCII.GetString(head, 0x10, 0x30).TrimEnd('\0'),
            Digest = head[0x40..0x50],
            DevHash = head[0x60..0x70],
            Flags = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x80)),
            BlockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x84)),
            FileSize = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(0x88)),
        };
    }

    /// <summary>Decrypts the whole file to a byte array. <paramref name="klicensee"/> is required for licensed EDATs.</summary>
    public static byte[] DecryptToArray(Stream source, byte[]? klicensee = null)
    {
        using var ms = new MemoryStream();
        Decrypt(source, ms, klicensee);
        return ms.ToArray();
    }

    /// <summary>Decrypts the file, writing the plaintext to <paramref name="destination"/>.</summary>
    public static void Decrypt(Stream source, Stream destination, byte[]? klicensee = null)
    {
        var npd = ParseHeader(source);

        if (npd.IsCompressed)
            throw new PkgFormatException("This EDAT is compressed; compressed decryption is not yet supported.");

        // Select the crypt key.
        byte[] cryptKey;
        if (npd.IsSdat)
        {
            cryptKey = new byte[16];
            for (int i = 0; i < 16; i++) cryptKey[i] = (byte)(npd.DevHash[i] ^ NpdKeys.SdatKey[i]);
        }
        else if (npd.IsFree)
        {
            cryptKey = NpdKeys.KlicFree;
        }
        else
        {
            cryptKey = klicensee ?? throw new PkgKeyException(
                $"'{npd.ContentId}' is a licensed EDAT (DRM {npd.LicenseText}); a RAP/klicensee is required.");
            if (cryptKey.Length != 16)
                throw new PkgKeyException("Klicensee must be 16 bytes.");
        }

        byte[] edatKey = npd.Version == 4 ? NpdKeys.EdatKey1 : NpdKeys.EdatKey0;
        bool encryptedKey = (npd.Flags & 0x00000008) != 0;
        bool aesCbc = (npd.Flags & 0x00000002) == 0; // else data is plain
        bool flag0x20 = (npd.Flags & 0x00000020) != 0;
        int metadataEntry = flag0x20 ? 0x20 : 0x10;

        int numBlocks = (int)((npd.FileSize + npd.BlockSize - 1) / npd.BlockSize);
        byte[] iv = npd.Version <= 1 ? new byte[16] : npd.Digest;

        using var aes = Aes.Create();
        aes.Padding = PaddingMode.None;

        for (int i = 0; i < numBlocks; i++)
        {
            int blockLen = (int)Math.Min(npd.BlockSize, npd.FileSize - (long)i * npd.BlockSize);
            int encLen = (blockLen + 15) & ~15;

            long dataOffset = flag0x20
                ? MetadataOffset + (long)i * (0x20 + npd.BlockSize) + 0x20        // metadata+data interleaved
                : MetadataOffset + (long)numBlocks * metadataEntry + (long)i * npd.BlockSize;

            byte[] enc = ReadAt(source, dataOffset, encLen);

            // Per-block key: dev_hash[0..12] (zeros for v<=1) then the block index, big-endian.
            byte[] bKey = new byte[16];
            if (npd.Version > 1) Array.Copy(npd.DevHash, bKey, 12);
            BinaryPrimitives.WriteUInt32BigEndian(bKey.AsSpan(12), (uint)i);

            byte[] keyResult = EcbEncrypt(aes, cryptKey, bKey);
            byte[] dataKey = encryptedKey ? CbcDecrypt(aes, edatKey, new byte[16], keyResult) : keyResult;

            byte[] dec = aesCbc ? CbcDecrypt(aes, dataKey, iv, enc) : enc;
            destination.Write(dec, 0, blockLen);
        }
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
