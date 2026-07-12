using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using PkgLens.Core;
using PkgLens.Core.Npd;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// Exercises the compressed-EDAT plumbing: the per-block metadata layout and the "stored" (head &gt; 0x80,
/// not-actually-compressed) branch of the LZ decompressor, end-to-end through <see cref="EdatFile"/> with
/// no keys. The range-coder LZ core itself is a faithful RPCS3 port awaiting a real compressed sample; it
/// is not exercised here (that needs an encoder we don't ship).
/// </summary>
public class EdatCompressionTests
{
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

    /// <summary>
    /// Builds a minimal <b>compressed</b> SDAT (version 1, self-keyed, data cipher disabled via flag
    /// 0x02) whose single block is an LZ "stored" block — so the compressed metadata path and the LZ
    /// stored branch run, without any AES or a real range-coded stream.
    /// </summary>
    private static byte[] BuildCompressedStoredSdat(byte[] payload)
    {
        const int metadataOffset = 0x100;
        const int blockSize = 0x400;

        // Stored LZ block: [head=0xFF][code=len BE u32][payload].
        var storedBlock = new byte[5 + payload.Length];
        storedBlock[0] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(storedBlock.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(storedBlock, 5);

        long dataOffset = metadataOffset + 0x20;         // data right after the one metadata entry
        int readLen = (storedBlock.Length + 15) & ~15;    // EdatFile reads a 16-aligned block
        int total = (int)dataOffset + readLen;
        var b = new byte[total];

        // NPD header.
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x00), 0x4E504400); // "NPD\0"
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x04), 1);           // version 1 (metadata not encrypted)
        // 0x08 license / 0x0C type left 0; SDAT is selected by the flag below.

        // EDAT header.
        // Flags: SDAT (0x01000000) | COMPRESSED (0x1) | 0x2 (disable the data AES-CBC, so the block is plain).
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x80), 0x01000003);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x84), blockSize);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x88), (ulong)payload.Length); // decompressed file size

        // Metadata entry (version 1: read directly, no unshuffle): [hash 0x10][offset u64][len s32][compEnd s32].
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(metadataOffset + 0x10), (ulong)dataOffset);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(metadataOffset + 0x18), storedBlock.Length);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(metadataOffset + 0x1C), 1); // compression_end != 0 => decompress

        storedBlock.CopyTo(b, (int)dataOffset);
        return b;
    }
}
