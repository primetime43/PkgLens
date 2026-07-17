using System.Buffers.Binary;
using System.IO.Compression;
using PkgLens.Core.Psp;
using Xunit;

namespace PkgLens.Core.Tests;

public class CsoWriterTests
{
    [Fact]
    public void Compress_WritesPspCompatibleCsoV1_ThatRoundTripsMixedBlocks()
    {
        byte[] iso = new byte[CsoWriter.BlockSize * 4];
        new Random(0xC501).NextBytes(iso.AsSpan(CsoWriter.BlockSize, CsoWriter.BlockSize));
        Array.Fill(iso, (byte)0x5A, CsoWriter.BlockSize * 2, CsoWriter.BlockSize);
        new Random(0xC502).NextBytes(iso.AsSpan(CsoWriter.BlockSize * 3, CsoWriter.BlockSize));

        using var source = new MemoryStream(iso);
        using var destination = new MemoryStream();
        CsoWriter.Compress(source, destination, iso.Length);
        byte[] cso = destination.ToArray();

        Assert.Equal("CISO"u8.ToArray(), cso[..4]);
        Assert.Equal(0x18u, BinaryPrimitives.ReadUInt32LittleEndian(cso.AsSpan(4)));
        Assert.Equal((ulong)iso.Length, BinaryPrimitives.ReadUInt64LittleEndian(cso.AsSpan(8)));
        Assert.Equal((uint)CsoWriter.BlockSize, BinaryPrimitives.ReadUInt32LittleEndian(cso.AsSpan(0x10)));
        Assert.Equal(1, cso[0x14]);

        byte[] restored = Decompress(cso);
        Assert.Equal(iso, restored);

        uint first = BinaryPrimitives.ReadUInt32LittleEndian(cso.AsSpan(0x18));
        uint second = BinaryPrimitives.ReadUInt32LittleEndian(cso.AsSpan(0x1C));
        Assert.Equal(0u, first & 0x80000000);
        Assert.NotEqual(0u, second & 0x80000000);
    }

    [Fact]
    public void Create_RejectsNonSectorAlignedIsoSize()
    {
        using var destination = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => CsoWriter.Create(destination, 123));
    }

    [Fact]
    public void Compress_TruncatedIso_PreservesEndOfStreamFailure()
    {
        using var source = new MemoryStream(new byte[CsoWriter.BlockSize]);
        using var destination = new MemoryStream();

        Assert.Throws<EndOfStreamException>(() =>
            CsoWriter.Compress(source, destination, CsoWriter.BlockSize * 2L));
    }

    private static byte[] Decompress(byte[] cso)
    {
        long totalBytes = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(cso.AsSpan(8)));
        int blockSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(cso.AsSpan(0x10)));
        int alignment = cso[0x15];
        int blockCount = checked((int)(totalBytes / blockSize));
        var output = new byte[totalBytes];

        for (int block = 0; block < blockCount; block++)
        {
            uint current = BinaryPrimitives.ReadUInt32LittleEndian(cso.AsSpan(0x18 + block * 4));
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(cso.AsSpan(0x18 + (block + 1) * 4));
            int start = checked((int)((current & 0x7FFFFFFF) << alignment));
            int end = checked((int)((next & 0x7FFFFFFF) << alignment));
            Span<byte> blockOutput = output.AsSpan(block * blockSize, blockSize);

            if ((current & 0x80000000) != 0)
            {
                cso.AsSpan(start, blockSize).CopyTo(blockOutput);
            }
            else
            {
                using var compressed = new MemoryStream(cso, start, end - start, writable: false);
                using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
                deflate.ReadExactly(blockOutput);
            }
        }
        return output;
    }
}
