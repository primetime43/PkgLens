using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PkgLens.Core.Shared.Crypto;

namespace PkgLens.Core.Ps3.Npd;

public static class EdatBuilder
{
    private const int MetadataOffset = 0x100;
    private static readonly byte[] NpdOmacKey2 = Convert.FromHexString("6BA52976EFDA16EF3C339FB2971E256B");
    private static readonly byte[] NpdOmacKey3 = Convert.FromHexString("9B515FEACF75064981AA604D91A54E97");
    private static readonly byte[] NpdKek = Convert.FromHexString("72F990788F9CFF745725F08E4C128387");

    public static byte[] BuildLicensed(ReadOnlySpan<byte> plaintext, string contentId, string fileName,
        byte[] klicensee, int blockSize = 0x4000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(klicensee);
        if (klicensee.Length != 16) throw new ArgumentException("Klicensee must be 16 bytes.", nameof(klicensee));
        if (blockSize <= 0 || (blockSize & 15) != 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        if (Encoding.ASCII.GetByteCount(contentId) > 0x30) throw new ArgumentException("Content ID is too long.", nameof(contentId));

        int blockCount = plaintext.Length == 0 ? 0 : checked((plaintext.Length + blockSize - 1) / blockSize);
        int dataOffset = checked(MetadataOffset + blockCount * 0x10);
        int totalSize = checked(dataOffset + blockCount * blockSize);
        var output = new byte[totalSize];
        BinaryPrimitives.WriteUInt32BigEndian(output, 0x4E504400);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x04), 3);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x08), 2);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x0C), 1);
        Encoding.ASCII.GetBytes(contentId).CopyTo(output.AsSpan(0x10));
        RandomNumberGenerator.Fill(output.AsSpan(0x40, 16));
        byte[] titleInput = Encoding.ASCII.GetBytes(contentId.PadRight(0x30, '\0') + fileName);
        AesCmac.Compute(NpdOmacKey3, titleInput).CopyTo(output.AsSpan(0x50));
        byte[] devKey = NpdKek.Zip(NpdOmacKey2, (left, right) => (byte)(left ^ right)).ToArray();
        AesCmac.Compute(devKey, output.AsSpan(0, 0x60)).CopyTo(output.AsSpan(0x60));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x84), checked((uint)blockSize));
        BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(0x88), checked((ulong)plaintext.Length));

        using var metadata = new MemoryStream();
        using var aes = Aes.Create();
        for (int block = 0; block < blockCount; block++)
        {
            int sourceOffset = block * blockSize;
            int length = Math.Min(blockSize, plaintext.Length - sourceOffset);
            int encryptedLength = (length + 15) & ~15;
            var padded = new byte[encryptedLength];
            plaintext.Slice(sourceOffset, length).CopyTo(padded);
            var blockKey = new byte[16];
            output.AsSpan(0x60, 12).CopyTo(blockKey);
            BinaryPrimitives.WriteUInt32BigEndian(blockKey.AsSpan(12), checked((uint)block));
            aes.Key = klicensee;
            byte[] keyResult = aes.EncryptEcb(blockKey, PaddingMode.None);
            aes.Key = keyResult;
            byte[] encrypted = aes.EncryptCbc(padded, output.AsSpan(0x40, 16), PaddingMode.None);
            encrypted.CopyTo(output, dataOffset + block * blockSize);
            byte[] hash = AesCmac.Compute(keyResult, encrypted);
            hash.CopyTo(output, MetadataOffset + block * 0x10);
            metadata.Write(hash);
        }

        AesCmac.Compute(klicensee, metadata.ToArray()).CopyTo(output.AsSpan(0x90));
        AesCmac.Compute(klicensee, output.AsSpan(0, 0xA0)).CopyTo(output.AsSpan(0xA0));
        return output;
    }
}
