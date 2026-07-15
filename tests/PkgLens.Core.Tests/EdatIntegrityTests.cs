using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Shared.Crypto;

namespace PkgLens.Core.Tests;

public class EdatIntegrityTests
{
    private const int BlockSize = 0x20;
    private static readonly byte[] Plaintext = Enumerable.Range(0, 53).Select(i => (byte)(i * 17 + 3)).ToArray();

    [Theory]
    [InlineData(0x00u)]
    [InlineData(0x10u)]
    [InlineData(0x30u)]
    [InlineData(0x08u)]
    public void Decrypt_VerifiesAllSupportedBlockHashModes(uint flags)
    {
        var fixture = BuildLicensedEdat(flags);

        byte[] decrypted = EdatFile.DecryptToArray(new MemoryStream(fixture.Data), fixture.Klicensee);

        Assert.Equal(Plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WrongKlicensee_IsReportedAsKeyFailure()
    {
        var fixture = BuildLicensedEdat(0);
        byte[] wrongKey = fixture.Klicensee.ToArray();
        wrongKey[0] ^= 0x80;

        var ex = Assert.Throws<PkgKeyException>(() =>
            EdatFile.DecryptToArray(new MemoryStream(fixture.Data), wrongKey));

        Assert.Contains("wrong", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RAP/klicensee", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_CorruptCiphertext_IsReportedAsBlockCorruption()
    {
        var fixture = BuildLicensedEdat(0);
        fixture.Data[fixture.FirstDataOffset + 7] ^= 0x40;

        var ex = Assert.Throws<PkgFormatException>(() =>
            EdatFile.DecryptToArray(new MemoryStream(fixture.Data), fixture.Klicensee));

        Assert.Contains("block 0", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("corrupted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_CorruptMetadata_IsReportedSeparately()
    {
        var fixture = BuildLicensedEdat(0);
        fixture.Data[0x100] ^= 0x01;

        var ex = Assert.Throws<PkgFormatException>(() =>
            EdatFile.DecryptToArray(new MemoryStream(fixture.Data), fixture.Klicensee));

        Assert.Contains("metadata", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("corrupted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EdatFixture BuildLicensedEdat(uint flags)
    {
        const int metadataOffset = 0x100;
        int blockCount = (Plaintext.Length + BlockSize - 1) / BlockSize;
        bool interleaved = (flags & 0x20) != 0;
        int metadataEntrySize = interleaved ? 0x20 : 0x10;
        int firstDataOffset = interleaved
            ? metadataOffset + metadataEntrySize
            : metadataOffset + blockCount * metadataEntrySize;
        int totalSize = interleaved
            ? metadataOffset + blockCount * (metadataEntrySize + BlockSize)
            : firstDataOffset + blockCount * BlockSize;

        var data = new byte[totalSize];
        byte[] klicensee = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        byte[] digest = Convert.FromHexString("102132435465768798A9BACBDCEDFE0F");
        byte[] devHash = Convert.FromHexString("A0A1A2A3A4A5A6A7A8A9AAABACADAEAF");

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x00), 0x4E504400);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x04), 4);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x08), 2);
        Encoding.ASCII.GetBytes("UP0001-TEST00000_00-EDATINTEGRITY000").CopyTo(data.AsSpan(0x10));
        digest.CopyTo(data, 0x40);
        devHash.CopyTo(data, 0x60);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x80), flags);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x84), BlockSize);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x88), (ulong)Plaintext.Length);

        using var metadata = new MemoryStream();
        using var aes = Aes.Create();
        aes.Padding = PaddingMode.None;
        byte[] edatKey = NpdKeys.EdatKey1;

        for (int block = 0; block < blockCount; block++)
        {
            int plaintextOffset = block * BlockSize;
            int payloadLength = Math.Min(BlockSize, Plaintext.Length - plaintextOffset);
            var padded = new byte[BlockSize];
            Plaintext.AsSpan(plaintextOffset, payloadLength).CopyTo(padded);

            var blockKey = new byte[16];
            devHash.AsSpan(0, 12).CopyTo(blockKey);
            BinaryPrimitives.WriteUInt32BigEndian(blockKey.AsSpan(12), (uint)block);
            byte[] keyResult = EcbEncrypt(aes, klicensee, blockKey);
            byte[] dataKey = (flags & 0x08) != 0
                ? CbcDecrypt(aes, edatKey, keyResult)
                : keyResult;
            byte[] encrypted = CbcEncrypt(aes, dataKey, digest, padded);

            int metadataPosition = interleaved
                ? metadataOffset + block * (metadataEntrySize + BlockSize)
                : metadataOffset + block * metadataEntrySize;
            int dataPosition = interleaved
                ? metadataPosition + metadataEntrySize
                : firstDataOffset + block * BlockSize;
            encrypted.CopyTo(data, dataPosition);

            byte[] hashSeed = (flags & 0x10) != 0
                ? EcbEncrypt(aes, klicensee, keyResult)
                : keyResult;
            byte[] hashKey = (flags & 0x08) != 0
                ? CbcDecrypt(aes, edatKey, hashSeed)
                : hashSeed;
            byte[] blockHash = ComputeBlockHash(flags, hashKey, encrypted);
            byte[] entry = EncodeMetadata(flags, blockHash);
            entry.CopyTo(data, metadataPosition);
            metadata.Write(entry);
        }

        byte[] headerHashKey = (flags & 0x08) != 0
            ? CbcDecrypt(aes, edatKey, klicensee)
            : klicensee;
        AesCmac.Compute(headerHashKey, metadata.ToArray()).CopyTo(data.AsSpan(0x90, 16));
        AesCmac.Compute(headerHashKey, data.AsSpan(0, 0xA0)).CopyTo(data.AsSpan(0xA0, 16));
        return new EdatFixture(data, klicensee, firstDataOffset);
    }

    private static byte[] ComputeBlockHash(uint flags, byte[] key, byte[] encrypted)
    {
        if ((flags & 0x10) == 0) return AesCmac.Compute(key, encrypted);
        byte[] hash = HMACSHA1.HashData(key, encrypted);
        return (flags & 0x20) != 0 ? hash : hash[..16];
    }

    private static byte[] EncodeMetadata(uint flags, byte[] hash)
    {
        if ((flags & 0x20) == 0) return hash[..16];

        var metadata = new byte[0x20];
        if (hash.Length == 20)
        {
            hash.AsSpan(16, 4).CopyTo(metadata.AsSpan(16));
            for (int i = 0; i < 16; i++) metadata[i] = (byte)(hash[i] ^ metadata[i + 16]);
        }
        else
        {
            hash.CopyTo(metadata, 0);
        }
        return metadata;
    }

    private static byte[] EcbEncrypt(Aes aes, byte[] key, byte[] input)
    {
        aes.Key = key;
        return aes.EncryptEcb(input, PaddingMode.None);
    }

    private static byte[] CbcEncrypt(Aes aes, byte[] key, byte[] iv, byte[] input)
    {
        aes.Key = key;
        return aes.EncryptCbc(input, iv, PaddingMode.None);
    }

    private static byte[] CbcDecrypt(Aes aes, byte[] key, byte[] input)
    {
        aes.Key = key;
        return aes.DecryptCbc(input, new byte[16], PaddingMode.None);
    }

    private sealed record EdatFixture(byte[] Data, byte[] Klicensee, int FirstDataOffset);
}
