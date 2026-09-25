using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PkgLens.Core.Shared.Crypto;

namespace PkgLens.Core.Ps3.Npd;

public sealed record EdatWriteOptions
{
    public bool IsSdat { get; init; }
    public int Version { get; init; } = 3;
    public int License { get; init; } = 3;
    public uint AppType { get; init; } = 1;
    public int BlockSize { get; init; } = 0x4000;
    public string ContentId { get; init; } = "";
    public string FileName { get; init; } = "";
    public byte[]? DeveloperKey { get; init; }
    // Quick rebuild preserves the original NPD identity, including dev hash and validity times.
    public byte[]? NpdTemplate { get; init; }
}

/// <summary>
/// Streaming finalized EDAT/SDAT writer. Block layouts follow Hykem's make_npdata;
/// compression is not emitted and Sony ECDSA signatures are not generated.
/// Every output is decrypted and SHA-256 compared before this method returns.
/// </summary>
public static class EdatWriter
{
    private static readonly byte[] TitleKey = Convert.FromHexString("9B515FEACF75064981AA604D91A54E97");
    private static readonly byte[] DevXor = Convert.FromHexString("6BA52976EFDA16EF3C339FB2971E256B");

    public static void WriteVerified(Stream plaintext, Stream output, EdatWriteOptions options,
        byte[]? contentKey = null, CancellationToken token = default, IProgress<double>? progress = null)
    {
        Validate(options);
        if (contentKey is { Length: not 16 }) throw new PkgKeyException("Content keys must contain 16 bytes.");
        if (!plaintext.CanRead || !plaintext.CanSeek || !output.CanRead || !output.CanWrite || !output.CanSeek)
            throw new ArgumentException("Encryption requires seekable plaintext and readable/writable output streams.");
        if (ReferenceEquals(plaintext, output)) throw new ArgumentException("Input and output must be separate streams.");
        token.ThrowIfCancellationRequested();
        if (!options.IsSdat && options.License == 3 && options.DeveloperKey is null && contentKey is not null)
            options = options with { DeveloperKey = contentKey };
        byte[] header = BuildHeader(options);
        uint flags = options.Version == 1 ? 0u : options.Version == 2 ? 0x0Cu : 0x3Cu;
        if (options.IsSdat) flags |= 0x01000000;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x80), flags);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x84), (uint)options.BlockSize);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x88), (ulong)plaintext.Length);
        byte[] key = options.IsSdat ? Xor(header.AsSpan(0x60, 16), NpdKeys.SdatKey) :
            options.License == 3 ? options.DeveloperKey ?? contentKey ?? NpdKeys.KlicFree :
            contentKey ?? throw new PkgKeyException("Licensed EDAT output requires its RAP-derived key or raw content key.");
        if (key.Length != 16) throw new PkgKeyException("Content keys must contain 16 bytes.");

        long blocks = plaintext.Length == 0 ? 0 : checked((plaintext.Length - 1) / options.BlockSize + 1);
        if (blocks > int.MaxValue) throw new PkgFormatException("Too many EDAT blocks.");
        bool interleaved = (flags & 0x20) != 0;
        int metadataSize = interleaved ? 32 : 16;
        byte[] versionKey = options.Version == 4 ? NpdKeys.EdatKey1 : NpdKeys.EdatKey0;
        using var aes = Aes.Create();
        byte[] Resolve(byte[] seed)
        {
            if ((flags & 8) == 0) return seed;
            aes.Key = versionKey;
            return aes.DecryptCbc(seed, new byte[16], PaddingMode.None);
        }
        byte[] headerKey = Resolve(key);
        using var metadataHash = new AesCmacAccumulator(headerKey);
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        output.SetLength(0);
        output.Write(header);
        plaintext.Position = 0;
        long end = 0x100;
        for (int block = 0; block < blocks; block++)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(options.BlockSize, plaintext.Length - plaintext.Position);
            byte[] padded = new byte[(count + 15) & ~15];
            plaintext.ReadExactly(padded.AsSpan(0, count));
            plainHash.AppendData(padded.AsSpan(0, count));
            byte[] blockSeed = new byte[16];
            if (options.Version > 1) header.AsSpan(0x60, 12).CopyTo(blockSeed);
            BinaryPrimitives.WriteInt32BigEndian(blockSeed.AsSpan(12), block);
            aes.Key = key;
            byte[] seed = aes.EncryptEcb(blockSeed, PaddingMode.None);
            byte[] hashSeed = interleaved ? aes.EncryptEcb(seed, PaddingMode.None) : seed;
            byte[] dataKey = Resolve(seed);
            byte[] hashKey = Resolve(hashSeed);
            aes.Key = dataKey;
            byte[] ciphertext = aes.EncryptCbc(padded, options.Version == 1 ? new byte[16] : header.AsSpan(0x40, 16), PaddingMode.None);
            byte[] hash = interleaved ? HMACSHA1.HashData(hashKey, ciphertext) : AesCmac.Compute(hashKey, ciphertext);
            byte[] metadata = new byte[metadataSize];
            if (interleaved)
            {
                RandomNumberGenerator.Fill(metadata.AsSpan(16));
                hash.AsSpan(16, 4).CopyTo(metadata.AsSpan(16));
                for (int i = 0; i < 16; i++) metadata[i] = (byte)(hash[i] ^ metadata[i + 16]);
            }
            else hash.CopyTo(metadata, 0);
            long metadataOffset = interleaved ? 0x100 + (long)block * (metadataSize + options.BlockSize) : 0x100 + (long)block * metadataSize;
            long dataOffset = interleaved ? metadataOffset + metadataSize : 0x100 + blocks * metadataSize + (long)block * options.BlockSize;
            output.Position = metadataOffset;
            output.Write(metadata);
            metadataHash.Append(metadata);
            output.Position = dataOffset;
            output.Write(ciphertext);
            end = output.Position;
            progress?.Report(plaintext.Length == 0 ? 75 : plaintext.Position * 75d / plaintext.Length);
        }
        output.Position = end;
        byte[] footer = new byte[16];
        string version = options.Version switch { 1 => "packager", 2 => "2.4.0.W", 3 => "3.3.0.W", _ => "4.0.0.W" };
        Encoding.ASCII.GetBytes((options.IsSdat ? "SDATA " : "EDATA ") + version).CopyTo(footer, 0);
        output.Write(footer);
        metadataHash.FinalizeHash().CopyTo(header, 0x90);
        AesCmac.Compute(headerKey, header.AsSpan(0, 0xA0)).CopyTo(header, 0xA0);
        output.Position = 0;
        output.Write(header);
        output.Flush();
        progress?.Report(80);
        token.ThrowIfCancellationRequested();
        using var verification = new DigestStream();
        EdatFile.Decrypt(output, verification, key, token);
        if (verification.BytesWritten != plaintext.Length ||
            !CryptographicOperations.FixedTimeEquals(plainHash.GetHashAndReset(), verification.Digest()))
            throw new PkgFormatException("Rebuilt EDAT failed plaintext round-trip verification.");
        progress?.Report(100);
    }

    public static EdatWriteOptions ForRebuild(Stream original, string fileName)
    {
        var info = EdatFile.ParseHeader(original);
        if (info.Version is < 1 or > 4)
            throw new PkgFormatException("Quick rebuild supports NPD versions 1–4. Use custom rebuild for this version.");
        original.Position = 0;
        byte[] template = new byte[0x80];
        original.ReadExactly(template);
        return new EdatWriteOptions
        {
            IsSdat = info.IsSdat, Version = info.Version, License = info.License, AppType = (uint)info.Type,
            BlockSize = info.BlockSize, ContentId = info.ContentId, FileName = fileName, NpdTemplate = template,
        };
    }

    private static byte[] BuildHeader(EdatWriteOptions options)
    {
        byte[] header = new byte[0x100];
        if (options.NpdTemplate is { } template)
        {
            if (template.Length != 0x80) throw new ArgumentException("NPD template must contain exactly 128 bytes.");
            template.CopyTo(header, 0);
            // Check identity fields independently of the replacement payload length.
            if (!header.AsSpan(0, 4).SequenceEqual("NPD\0"u8) ||
                BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)) != options.Version ||
                BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8)) != options.License ||
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)) != options.AppType ||
                Encoding.ASCII.GetString(header, 0x10, 0x30).TrimEnd('\0') != options.ContentId)
                throw new PkgFormatException("Quick rebuild settings do not match the original NPD identity.");
            if (!options.IsSdat && !CryptographicOperations.FixedTimeEquals(TitleHash(header, options.FileName), header.AsSpan(0x50, 16)))
                throw new PkgFormatException("Quick rebuild must retain the original EDAT filename and valid title hash. Use custom rebuild to change it.");
            return header;
        }
        "NPD\0"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), options.Version);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), options.License);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), options.AppType);
        Encoding.ASCII.GetBytes(options.ContentId).CopyTo(header, 0x10);
        RandomNumberGenerator.Fill(header.AsSpan(0x40, 16));
        TitleHash(header, options.FileName).CopyTo(header, 0x50);
        byte[] devKey = options.DeveloperKey ?? NpdKeys.KlicFree;
        AesCmac.Compute(Xor(devKey, DevXor), header.AsSpan(0, 0x60)).CopyTo(header, 0x60);
        return header;
    }

    public static void Validate(EdatWriteOptions options)
    {
        if (options.Version is < 1 or > 4 || (options.IsSdat && options.Version == 1))
            throw new ArgumentException("EDAT supports versions 1–4; SDAT supports versions 2–4.");
        if (options.License > 3 || options.License < (options.IsSdat ? 0 : 1)) throw new ArgumentException("License must be Network (1), Local (2), or Free (3).");
        if (options.BlockSize is < 1024 or > 32768 || (options.BlockSize & (options.BlockSize - 1)) != 0)
            throw new ArgumentException("Block size must be 1, 2, 4, 8, 16, or 32 KiB.");
        if ((!options.IsSdat && string.IsNullOrWhiteSpace(options.ContentId)) || options.ContentId.Length > 48 ||
            options.ContentId.Any(c => c < 32 || c > 126)) throw new ArgumentException("Content ID must contain 1–48 printable ASCII characters.");
        if (string.IsNullOrWhiteSpace(options.FileName) || options.FileName.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Specify the output filename without a directory.");
        if (options.DeveloperKey is { Length: not 16 }) throw new ArgumentException("Developer key must contain 16 bytes.");
    }

    private static byte[] TitleHash(byte[] header, string fileName)
    {
        byte[] name = Encoding.UTF8.GetBytes(fileName);
        byte[] buffer = new byte[48 + name.Length];
        header.AsSpan(0x10, 48).CopyTo(buffer);
        name.CopyTo(buffer, 48);
        return AesCmac.Compute(TitleKey, buffer);
    }

    private static byte[] Xor(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        byte[] result = new byte[16];
        for (int i = 0; i < 16; i++) result[i] = (byte)(left[i] ^ right[i]);
        return result;
    }

    private sealed class DigestStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesWritten { get; private set; }
        public byte[] Digest() => _hash.GetHashAndReset();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { _hash.AppendData(buffer); BytesWritten += buffer.Length; }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
