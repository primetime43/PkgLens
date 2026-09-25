using System.Buffers.Binary;
using System.Security.Cryptography;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public class EncryptedSelfBuilderTests
{
    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 3)]
    [InlineData(true, true, 2)]
    [InlineData(true, false, 1)]
    public void EncryptedOutputPreservesElf(bool npdrm, bool compressed, uint license)
    {
        byte[] elf = DevKlicFixture.Elf(4096);
        "Executable payload to preserve"u8.CopyTo(elf.AsSpan(0x100));
        byte[]? key = license == 3 ? null : DevKlicFixture.Key;
        var options = new EncryptedSelfBuilder.Options
        {
            Metadata = new() { Npdrm = npdrm, ContentId = DevKlicFixture.ContentId,
                NpLicenseType = license, CompressSegments = compressed }, Klicensee = key,
        };
        byte[] self = EncryptedSelfBuilder.Build(elf, options);
        Assert.Equal(elf, SelfDecryptor.Decrypt(self, key).Elf);
        Assert.Equal(0x0A, BinaryPrimitives.ReadUInt16BigEndian(self.AsSpan(8)));
        Assert.False(self.AsSpan().IndexOf("Executable payload to preserve"u8) >= 0);
        Assert.NotEqual(self, EncryptedSelfBuilder.Build(elf, options));
        if (npdrm)
            Assert.Throws<PkgFormatException>(() => SelfDecryptor.Decrypt(self, new byte[16]));
    }

    [Fact]
    public void EveryBundledRevisionRoundTrips()
    {
        foreach (bool np in new[] { false, true })
        foreach (ushort revision in EncryptedSelfBuilder.SupportedRevisions(np))
        {
            byte[] elf = DevKlicFixture.Elf(512);
            byte[] self = EncryptedSelfBuilder.Build(elf, new() { KeyRevision = revision,
                Metadata = new() { Npdrm = np, ContentId = DevKlicFixture.ContentId } });
            Assert.Equal(elf, SelfDecryptor.Decrypt(self).Elf);
        }
    }

    [Fact]
    public void RejectsUnknownRevisionMissingLicenseAndUnrepresentedData()
    {
        byte[] elf = DevKlicFixture.Elf(512);
        Assert.Throws<PkgFormatException>(() => EncryptedSelfBuilder.Build(elf, new() { KeyRevision = 0x7F }));
        Assert.Throws<PkgFormatException>(() => EncryptedSelfBuilder.Build(elf, new() {
            Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 2 } }));
        elf[0x90] = 0xAB;
        Assert.Throws<PkgFormatException>(() => EncryptedSelfBuilder.Build(elf, new()));
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x48), ulong.MaxValue);
        Assert.Throws<PkgFormatException>(() => EncryptedSelfBuilder.Build(elf, new()));
    }

    [Fact]
    public void PreservesSprxMultipleSegmentsAndSectionHeaders()
    {
        byte[] elf = DevKlicFixture.Elf(4096);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x10), 0xFFA4);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x38), 2);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x60), 0x600);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x78), 0x700000A4);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x80), 0x700);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x98), 0x8C0);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x28), 0xFC0);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x3A), 0x40);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x3C), 1);
        RandomNumberGenerator.Fill(elf.AsSpan(0x700));
        var self = EncryptedSelfBuilder.Build(elf, new() { Metadata = new() { Npdrm = true,
            ContentId = DevKlicFixture.ContentId }, FileName = "module.sprx" });
        Assert.Equal(elf, SelfDecryptor.Decrypt(self).Elf);
    }

    [Fact]
    public void HonorsNonstandardProgramHeaderPlacementAndCancellation()
    {
        byte[] elf = DevKlicFixture.Elf(512);
        elf.AsSpan(0x40, 0x38).CopyTo(elf.AsSpan(0x80));
        elf.AsSpan(0x40, 0x38).Clear();
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x20), 0x80);
        Assert.Equal(elf, SelfDecryptor.Decrypt(EncryptedSelfBuilder.Build(elf, new())).Elf);
        Assert.Throws<OperationCanceledException>(() => EncryptedSelfBuilder.Build(elf, new(), new CancellationToken(true)));
    }

    [Fact]
    public void MetadataSectionHmacAndNpdrmFilenameHashesValidate()
    {
        byte[] elf = DevKlicFixture.Elf(2048);
        byte[] self = EncryptedSelfBuilder.Build(elf, new() { FileName = "MODULE.SPRX",
            Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 2 },
            Klicensee = DevKlicFixture.Key });
        int ci = (int)BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(0x58));
        // First control block is flags, second is the full-file SHA1, third is NPDRM.
        Assert.Equal(SHA1.HashData(elf), self.AsSpan(ci + 0x30 + 36, 20).ToArray());
        int npd = ci + 0x30 + 0x40 + 16;
        byte[] filenameMessage = self.AsSpan(npd + 16, 48).ToArray().Concat("MODULE.SPRX"u8.ToArray()).ToArray();
        Assert.Equal(AesCmac.Compute(Convert.FromHexString("9B515FEACF75064981AA604D91A54E97"), filenameMessage),
            self.AsSpan(npd + 0x50, 16).ToArray());
        byte[] controlKey = Convert.FromHexString("6BA52976EFDA16EF3C339FB2971E256B");
        for (int i = 0; i < 16; i++) controlKey[i] ^= DevKlicFixture.Key[i];
        Assert.Equal(AesCmac.Compute(controlKey, self.AsSpan(npd, 0x60)), self.AsSpan(npd + 0x60, 16).ToArray());

        int metaInfoOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(12)) + 32;
        int headerLength = (int)BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(16));
        using var aes = Aes.Create(); aes.Key = SelfKeyset.NpKlicKey;
        aes.Key = aes.DecryptEcb(DevKlicFixture.Key, PaddingMode.None);
        byte[] metaInfo = aes.DecryptCbc(self.AsSpan(metaInfoOffset, 64), new byte[16], PaddingMode.None);
        var root = SelfKeyset.Find(8, 0x0A)!; aes.Key = root.Erk;
        metaInfo = aes.DecryptCbc(metaInfo, root.Riv, PaddingMode.None);
        byte[] metadata = CounterCrypt(metaInfo[..16], metaInfo[32..48],
            self.AsSpan(metaInfoOffset + 64, headerLength - metaInfoOffset - 64).ToArray());
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(metadata.AsSpan(12));
        int keys = 32 + count * 48;
        for (int i = 0; i < count; i++)
        {
            var section = metadata.AsSpan(32 + i * 48, 48);
            int offset = (int)BinaryPrimitives.ReadUInt64BigEndian(section);
            int length = (int)BinaryPrimitives.ReadUInt64BigEndian(section[8..]);
            int hashIndex = (int)BinaryPrimitives.ReadUInt32BigEndian(section[28..]);
            int keyIndex = (int)BinaryPrimitives.ReadUInt32BigEndian(section[36..]);
            int ivIndex = (int)BinaryPrimitives.ReadUInt32BigEndian(section[40..]);
            byte[] payload = CounterCrypt(metadata.AsSpan(keys + keyIndex * 16, 16).ToArray(),
                metadata.AsSpan(keys + ivIndex * 16, 16).ToArray(), self.AsSpan(offset, length).ToArray());
            Assert.Equal(HMACSHA1.HashData(metadata.AsSpan(keys + hashIndex * 16 + 32, 64), payload),
                metadata.AsSpan(keys + hashIndex * 16, 20).ToArray());
        }
        int signatureOffset = (int)BinaryPrimitives.ReadUInt64BigEndian(metadata) - metaInfoOffset - 64;
        Assert.All(metadata.AsSpan(signatureOffset, 48).ToArray(), b => Assert.Equal(0, b));
    }

    private static byte[] CounterCrypt(byte[] key, byte[] counter, byte[] data)
    {
        using var aes = Aes.Create(); aes.Key = key;
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            byte[] block = aes.EncryptEcb(counter, PaddingMode.None);
            for (int i = 0; i < Math.Min(16, data.Length - offset); i++) data[offset + i] ^= block[i];
            for (int i = 15; i >= 0; i--) { if (++counter[i] != 0) break; }
        }
        return data;
    }
}
