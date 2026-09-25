using System.Buffers.Binary;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public class LegacySelfSigningTests
{
    [Fact]
    public void VerifiesIndependentScetoolSignatureAndRejectsAlteredDigest()
    {
        // Captured from the independent scetool APP 0A signing experiment, not this writer.
        byte[] digest = Convert.FromHexString("48A8B5251980F473DE04F65F5EFC8F67A5AA6C13");
        byte[] signature = Convert.FromHexString("001FA98D826FC49888B85186B94E25EC9BA2C218BA0031BC83C09163EC80ECA35A1BD6D1447716ED22D3");
        Assert.True(LegacySelfSigning.VerifyHash(4, 0x0A, digest, signature));
        digest[0] ^= 1;
        Assert.False(LegacySelfSigning.VerifyHash(4, 0x0A, digest, signature));
        Assert.False(LegacySelfSigning.VerifyHash(4, 0x0A, digest, new byte[42]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryLegacyProfileSignsAndPreservesElf(bool npdrm)
    {
        var profiles = LegacySelfSigning.Profiles(npdrm);
        Assert.Equal(npdrm ? new ushort[] { 1, 4, 7, 10 } : new ushort[] { 0, 1, 4, 7, 10 }, profiles.Select(p => p.Revision));
        foreach (var profile in profiles)
        foreach (bool compress in new[] { false, true })
        {
            byte[] elf = DevKlicFixture.Elf(4096);
            "Executable code"u8.CopyTo(elf.AsSpan(0x100));
            byte[] self = EncryptedSelfBuilder.Build(elf, new() { SignHeader = true, KeyRevision = profile.Revision,
                Metadata = new() { Npdrm = npdrm, ContentId = DevKlicFixture.ContentId, CompressSegments = compress } });
            Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(self));
            Assert.Equal(elf, SelfDecryptor.Decrypt(self).Elf);
            self[0x70] ^= 1; // Authentication ID is plaintext but must be signed.
            Assert.Equal(SelfSignatureStatus.Invalid, SelfSignature.VerifyHeader(self));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NpdrmSignsWithIndependentContentKey(uint license)
    {
        var elf = DevKlicFixture.Elf(1024);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x10), 0xFFA4);
        var self = EncryptedSelfBuilder.Build(elf, new() { SignHeader = true, Klicensee = DevKlicFixture.Key,
            FileName = "module.sprx", Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = license } });
        Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(self, DevKlicFixture.Key));
        Assert.Throws<PkgFormatException>(() => SelfSignature.VerifyHeader(self, new byte[16]));
        Assert.Equal(elf, SelfDecryptor.Decrypt(self, DevKlicFixture.Key).Elf);
    }

    [Fact]
    public void NoSilentFallbackAndUnsignedModesRemainAvailable()
    {
        byte[] elf = DevKlicFixture.Elf(1024);
        Assert.Throws<PkgFormatException>(() => EncryptedSelfBuilder.Build(elf, new() { SignHeader = true, KeyRevision = 0x10 }));
        Assert.Equal(SelfSignatureStatus.Absent, SelfSignature.VerifyHeader(EncryptedSelfBuilder.Build(elf, new())));
        Assert.Equal(SelfSignatureStatus.Absent, SelfSignature.VerifyHeader(SelfBuilder.MakeFakeSelf(elf)));
        Assert.Equal(SelfSignatureStatus.UnsupportedProfile,
            SelfSignature.VerifyHeader(EncryptedSelfBuilder.Build(elf, new() { KeyRevision = 0x10 })));
    }

    [Fact]
    public void RejectsTamperedSignatureAndInvalidMetadataBounds()
    {
        byte[] self = EncryptedSelfBuilder.Build(DevKlicFixture.Elf(1024), new() { SignHeader = true });
        int mi = (int)BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(12)) + 32;
        int signatureOffset = mi + 64 + 32 + 48 + 128 + 48; // One PHDR, eight keys, capability header.
        self[signatureOffset + 1] ^= 1; // CTR ciphertext changes only that signature byte.
        Assert.Equal(SelfSignatureStatus.Invalid, SelfSignature.VerifyHeader(self));
        BinaryPrimitives.WriteUInt32BigEndian(self.AsSpan(12), uint.MaxValue);
        Assert.Throws<PkgFormatException>(() => SelfSignature.VerifyHeader(self));
    }
}
