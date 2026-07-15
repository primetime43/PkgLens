using PkgLens.Core.Shared.Crypto;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Ps3.Npd;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// Exercises compressed EDAT blocks end-to-end, including both the stored representation and a fixed
/// range-coded LZRC golden vector.
/// </summary>
public class EdatCompressionTests
{
    private static readonly byte[] GoldenCompressedBlock = Convert.FromHexString(
        "2F8EDB15BD8A7632DB0C8C5B32465AB282B874F79176D022044F732DD36A8E74" +
        "4F1BB32A1D37828B2E2BC2129DB547CAD91BA588F55134A1BDB9116E17495F71" +
        "7E170C179330944A36E9C966B3BE844E07A853B7C4057FFDE7116C64261373E4" +
        "CB519385A93037A9865B520B88CEEEFF34C2666FF6A7AC78980E2F81C3302422");

    private static readonly byte[] GoldenPlaintext = Convert.FromHexString(
        "E292DB629C742FDD5B5CF5F3888DA793A112F3F7FD828E53F384E1DF9181F38C" +
        "9A558BFB959287FBE526848191989A558BE6FACE8B90A087B6878799859AA4B08" +
        "4FD99838487978781FB979B90949C9190849D9FB66895279B86249D929CFA9A99" +
        "9B9B29249898A499989AB09BA7829A98A6A0989B989A9A97959197989B2D88");

    [Fact]
    public void EdatLz_RangeCodedGoldenVector_DecompressesExactly()
    {
        Assert.True(GoldenCompressedBlock[0] <= 0x80); // range-coded, never the stored-block shortcut
        var output = new byte[GoldenPlaintext.Length];

        int length = EdatLz.Decompress(output, GoldenCompressedBlock, output.Length);

        Assert.Equal(GoldenPlaintext.Length, length);
        Assert.Equal(GoldenPlaintext, output);
    }

    [Fact]
    public void Decrypt_CompressedSdat_RangeCodedGolden_RoundTrips()
    {
        byte[] edat = BuildCompressedSdat(
            GoldenCompressedBlock, blockSize: GoldenPlaintext.Length, fileSize: GoldenPlaintext.Length);

        byte[] plaintext = EdatFile.DecryptToArray(new MemoryStream(edat));

        Assert.Equal(GoldenPlaintext, plaintext);
    }

    [Fact]
    public void EdatLz_StoredBlock_CopiesRawPayload()
    {
        byte[] payload = Enumerable.Range(0, 40).Select(i => (byte)(i * 5 + 1)).ToArray();

        // head > 0x80 => "stored": code (in[1..5], big-endian) is the length, payload starts at in[5].
        var input = new byte[5 + payload.Length + 8];
        input[0] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(input, 5);

        var output = new byte[256];
        int n = EdatLz.Decompress(output, input, output.Length);

        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, output[..payload.Length]);
    }

    [Fact]
    public void EdatLz_StoredBlock_TooLargeForOutput_ReturnsError()
    {
        var input = new byte[16];
        input[0] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(1), 1000); // code > size
        Assert.True(EdatLz.Decompress(new byte[8], input, 8) < 0);
    }

    [Fact]
    public void Decrypt_CompressedSdat_StoredBlock_RoundTrips()
    {
        byte[] payload = Enumerable.Range(0, 20).Select(i => (byte)(0xA0 + i)).ToArray();
        byte[] edat = BuildCompressedStoredSdat(payload);

        byte[] got = EdatFile.DecryptToArray(new MemoryStream(edat));

        Assert.Equal(payload, got);
    }

    [Fact]
    public void Decrypt_CompressedBlock_CorruptMetadata_FailsIntegrityCheck()
    {
        byte[] edat = BuildCompressedStoredSdat(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        BinaryPrimitives.WriteInt32BigEndian(edat.AsSpan(0x100 + 0x18), 0x7FFFFFFF);

        var ex = Assert.Throws<PkgFormatException>(() => EdatFile.DecryptToArray(new MemoryStream(edat)));
        Assert.Contains("metadata", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a minimal <b>compressed</b> SDAT (version 1, self-keyed, data cipher disabled via flag
    /// 0x02) whose single block is an LZ "stored" block — so the compressed metadata path and the LZ
    /// stored branch run, without any AES or a real range-coded stream.
    /// </summary>
    private static byte[] BuildCompressedStoredSdat(byte[] payload)
    {
        // Stored LZ block: [head=0xFF][code=len BE u32][payload].
        var storedBlock = new byte[5 + payload.Length];
        storedBlock[0] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(storedBlock.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(storedBlock, 5);

        return BuildCompressedSdat(storedBlock, blockSize: 0x400, fileSize: payload.Length);
    }

    private static byte[] BuildCompressedSdat(byte[] compressedBlock, int blockSize, int fileSize)
    {
        const int metadataOffset = 0x100;
        if (compressedBlock.Length > blockSize)
            throw new ArgumentException("The compressed block must fit within the EDAT block size.", nameof(compressedBlock));

        long dataOffset = metadataOffset + 0x20;         // data right after the one metadata entry
        int readLen = (compressedBlock.Length + 15) & ~15; // EdatFile reads a 16-aligned block
        int total = (int)dataOffset + readLen;
        var b = new byte[total];

        // NPD header.
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x00), 0x4E504400); // "NPD\0"
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x04), 1);           // version 1 (metadata not encrypted)
        // 0x08 license / 0x0C type left 0; SDAT is selected by the flag below.

        // EDAT header.
        // Flags: SDAT (0x01000000) | COMPRESSED (0x1) | 0x2 (disable the data AES-CBC, so the block is plain).
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x80), 0x01000003);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x84), (uint)blockSize);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x88), (ulong)fileSize); // decompressed file size

        // Metadata entry (version 1: read directly, no unshuffle): [hash 0x10][offset u64][len s32][compEnd s32].
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(metadataOffset + 0x10), (ulong)dataOffset);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(metadataOffset + 0x18), compressedBlock.Length);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(metadataOffset + 0x1C), 1); // compression_end != 0 => decompress

        compressedBlock.CopyTo(b, (int)dataOffset);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = NpdKeys.SdatKey;
        byte[] blockKey = aes.EncryptEcb(new byte[16], PaddingMode.None);
        AesCmac.Compute(blockKey, b.AsSpan((int)dataOffset, readLen)).CopyTo(b.AsSpan(metadataOffset, 16));
        AesCmac.Compute(NpdKeys.SdatKey, b.AsSpan(metadataOffset, 0x20)).CopyTo(b.AsSpan(0x90, 16));
        AesCmac.Compute(NpdKeys.SdatKey, b.AsSpan(0, 0xA0)).CopyTo(b.AsSpan(0xA0, 16));
        return b;
    }
}
