using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PkgLens.Core.Shared.Crypto;

namespace PkgLens.Core.Ps2;

public enum Ps2ClassicMode
{
    Cex,
    Dex,
}

public sealed record Ps2ClassicImageInfo(
    string ContentId, int SegmentSize, long IsoSize, Ps2ClassicMode? Mode = null);

public sealed record Ps2ClassicProgress(string Stage, long CompletedBytes, long TotalBytes)
{
    public double Percentage => TotalBytes == 0 ? 100 : CompletedBytes * 100d / TotalBytes;
}

public static class Ps2ClassicImage
{
    public const string PlaceholderContentId = "2P0001-PS2U10000_00-0000111122223333";
    public const string ReactPsnContentId = "UP0001-RPS200000_00-0000000000000000";
    public static readonly byte[] PlaceholderKlicensee = Convert.FromHexString("E4E54FD67C16C316F47829A30484D843");
    public static readonly byte[] ReactPsnKlicensee = Convert.FromHexString("D8B488770599DDF738B9AC1D2101FA57");

    private const int DefaultSegmentSize = 0x4000;
    private const int MetadataEntrySize = 0x20;
    private const int ChildSegments = DefaultSegmentSize / MetadataEntrySize;
    private static readonly byte[] CexDataKey = Convert.FromHexString("1017823463F468C1AA41D700B140F257");
    private static readonly byte[] CexMetaKey = Convert.FromHexString("389DCBA5203C8159ECF94C9393164CC9");
    private static readonly byte[] DexDataKey = Convert.FromHexString("74FF7E5D1D7B96943BEFDCFA81FC2007");
    private static readonly byte[] DexMetaKey = Convert.FromHexString("2B05F7C7AFD1B169D62586503AEA9798");
    private static readonly byte[] NpdOmacKey2 = Convert.FromHexString("6BA52976EFDA16EF3C339FB2971E256B");
    private static readonly byte[] NpdOmacKey3 = Convert.FromHexString("9B515FEACF75064981AA604D91A54E97");
    private static readonly byte[] NpdKek = Convert.FromHexString("72F990788F9CFF745725F08E4C128387");

    public static bool IsPs2Classic(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == 0x50533200;

    public static Ps2ClassicImageInfo ParseHeader(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("PS2 Classic input must be readable and seekable.", nameof(source));
        long position = source.Position;
        try
        {
            source.Position = 0;
            var header = new byte[0x90];
            source.ReadExactly(header);
            if (!IsPs2Classic(header))
                throw new PkgFormatException("Not a PS2 Classic ISO.BIN.ENC image (bad PS2 header magic).");
            int segmentSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x84)));
            long isoSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x88)));
            if (segmentSize != DefaultSegmentSize)
                throw new PkgFormatException($"Unsupported PS2 Classic segment size 0x{segmentSize:X}; expected 0x{DefaultSegmentSize:X}.");
            if (isoSize <= 0)
                throw new PkgFormatException("PS2 Classic image reports an empty or invalid ISO size.");
            string contentId = Encoding.ASCII.GetString(header, 0x10, 0x30).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(contentId))
                throw new PkgFormatException("PS2 Classic image has no content ID.");
            long segments = checked((isoSize + segmentSize - 1) / segmentSize);
            long groups = checked((segments + ChildSegments - 1) / ChildSegments);
            long minimumLength = checked(segmentSize + groups * segmentSize + segments * segmentSize);
            if (source.Length < minimumLength)
                throw new PkgFormatException($"Truncated PS2 Classic image: expected at least {minimumLength:n0} bytes, found {source.Length:n0}.");
            return new(contentId, segmentSize, isoSize);
        }
        finally
        {
            source.Position = position;
        }
    }

    public static Ps2ClassicImageInfo Decrypt(Stream source, Stream destination, byte[] klicensee,
        CancellationToken cancellationToken = default, IProgress<Ps2ClassicProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("ISO destination must be writable.", nameof(destination));
        ValidateKlicensee(klicensee);
        Ps2ClassicImageInfo info = ParseHeader(source);
        Ps2ClassicMode mode = DetectMode(source, info, klicensee);
        (byte[] dataKey, byte[] metaKey) = DeriveKeys(mode, klicensee);
        long remaining = info.IsoSize;
        long completed = 0;
        uint expectedSegment = 0;
        source.Position = info.SegmentSize;
        var metadata = new byte[info.SegmentSize];
        var encrypted = new byte[info.SegmentSize];

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.ReadExactly(metadata);
            byte[] plainMetadata = DecryptCbc(metaKey, metadata);
            int count = (int)Math.Min(ChildSegments,
                (remaining + info.SegmentSize - 1) / info.SegmentSize);
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.ReadExactly(encrypted);
                VerifySegment(plainMetadata.AsSpan(index * MetadataEntrySize, MetadataEntrySize),
                    encrypted, expectedSegment++);
                byte[] plaintext = DecryptCbc(dataKey, encrypted);
                int write = (int)Math.Min(info.SegmentSize, remaining);
                destination.Write(plaintext, 0, write);
                remaining -= write;
                completed += write;
                progress?.Report(new("Decrypting PS2 disc", completed, info.IsoSize));
            }
        }
        return info with { Mode = mode };
    }

    public static Ps2ClassicImageInfo Encrypt(Stream iso, Stream destination, byte[] klicensee,
        string contentId = PlaceholderContentId, Ps2ClassicMode mode = Ps2ClassicMode.Cex,
        string fileName = "ISO.BIN.ENC", CancellationToken cancellationToken = default,
        IProgress<Ps2ClassicProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateKlicensee(klicensee);
        if (!iso.CanRead || !iso.CanSeek) throw new ArgumentException("ISO input must be readable and seekable.", nameof(iso));
        if (!destination.CanWrite) throw new ArgumentException("Encrypted destination must be writable.", nameof(destination));
        if (iso.Length <= 0) throw new PkgFormatException("Cannot encrypt an empty PS2 ISO.");
        if (Encoding.ASCII.GetByteCount(contentId) > 0x30) throw new ArgumentException("Content ID is too long.", nameof(contentId));

        long isoSize = iso.Length;
        byte[] header = BuildHeader(contentId, fileName, isoSize);
        destination.Write(header);
        (byte[] dataKey, byte[] metaKey) = DeriveKeys(mode, klicensee);
        var plaintext = new byte[DefaultSegmentSize];
        var metadata = new byte[DefaultSegmentSize];
        var encryptedSegments = new List<byte[]>(ChildSegments);
        long completed = 0;
        uint segmentNumber = 0;
        iso.Position = 0;

        while (completed < isoSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(metadata);
            encryptedSegments.Clear();
            for (int index = 0; index < ChildSegments && completed < isoSize; index++)
            {
                Array.Clear(plaintext);
                int wanted = (int)Math.Min(DefaultSegmentSize, isoSize - completed);
                iso.ReadExactly(plaintext.AsSpan(0, wanted));
                byte[] encrypted = EncryptCbc(dataKey, plaintext);
                encryptedSegments.Add(encrypted);
                SHA1.HashData(encrypted).CopyTo(metadata.AsSpan(index * MetadataEntrySize, 20));
                BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(index * MetadataEntrySize + 0x14), segmentNumber++);
                completed += wanted;
                progress?.Report(new("Encrypting PS2 disc for CFW", completed, isoSize));
            }
            destination.Write(EncryptCbc(metaKey, metadata));
            foreach (byte[] encrypted in encryptedSegments) destination.Write(encrypted);
        }
        return new(contentId, DefaultSegmentSize, isoSize, mode);
    }

    private static Ps2ClassicMode DetectMode(Stream source, Ps2ClassicImageInfo info, byte[] klicensee)
    {
        long position = source.Position;
        try
        {
            source.Position = info.SegmentSize;
            var metadata = new byte[info.SegmentSize];
            var encrypted = new byte[info.SegmentSize];
            source.ReadExactly(metadata);
            source.ReadExactly(encrypted);
            foreach (Ps2ClassicMode mode in Enum.GetValues<Ps2ClassicMode>())
            {
                (_, byte[] metaKey) = DeriveKeys(mode, klicensee);
                byte[] plainMetadata = DecryptCbc(metaKey, metadata);
                if (SegmentMatches(plainMetadata.AsSpan(0, MetadataEntrySize), encrypted, 0)) return mode;
            }
            throw new PkgKeyException($"PS2 Classic metadata authentication failed for '{info.ContentId}'; the RAP/klicensee is wrong or the image is corrupted.");
        }
        finally
        {
            source.Position = position;
        }
    }

    private static void VerifySegment(ReadOnlySpan<byte> metadata, byte[] encrypted, uint expectedSegment)
    {
        if (!SegmentMatches(metadata, encrypted, expectedSegment))
            throw new PkgFormatException($"PS2 Classic segment {expectedSegment} failed its SHA-1 metadata check; the image is corrupted.");
    }

    private static bool SegmentMatches(ReadOnlySpan<byte> metadata, byte[] encrypted, uint expectedSegment)
    {
        byte[] hash = SHA1.HashData(encrypted);
        uint stored = BinaryPrimitives.ReadUInt32BigEndian(metadata[0x14..]);
        return CryptographicOperations.FixedTimeEquals(hash, metadata[..20]) &&
               (stored & 0x00FFFFFF) == (expectedSegment & 0x00FFFFFF);
    }

    private static (byte[] Data, byte[] Meta) DeriveKeys(Ps2ClassicMode mode, byte[] klicensee)
    {
        byte[] dataMaster = mode == Ps2ClassicMode.Cex ? CexDataKey : DexDataKey;
        byte[] metaMaster = mode == Ps2ClassicMode.Cex ? CexMetaKey : DexMetaKey;
        return (EncryptCbc(dataMaster, klicensee), EncryptCbc(metaMaster, klicensee));
    }

    private static byte[] BuildHeader(string contentId, string fileName, long isoSize)
    {
        var header = new byte[DefaultSegmentSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x50533200);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x04), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x06), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x08), 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x0C), 1);
        Encoding.ASCII.GetBytes(contentId).CopyTo(header.AsSpan(0x10));
        RandomNumberGenerator.Fill(header.AsSpan(0x40, 16));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x84), DefaultSegmentSize);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x88), checked((ulong)isoSize));
        byte[] titleInput = Encoding.ASCII.GetBytes(contentId.PadRight(0x30, '\0') + fileName);
        AesCmac.Compute(NpdOmacKey3, titleInput).CopyTo(header.AsSpan(0x50));
        byte[] devKey = NpdKek.Zip(NpdOmacKey2, (left, right) => (byte)(left ^ right)).ToArray();
        AesCmac.Compute(devKey, header.AsSpan(0, 0x60)).CopyTo(header.AsSpan(0x60));
        return header;
    }

    private static byte[] EncryptCbc(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }

    private static byte[] DecryptCbc(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, new byte[16], PaddingMode.None);
    }

    private static void ValidateKlicensee(byte[] klicensee)
    {
        ArgumentNullException.ThrowIfNull(klicensee);
        if (klicensee.Length != 16) throw new PkgKeyException("PS2 Classic klicensee must be 16 bytes.");
    }
}
