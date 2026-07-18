using System.Buffers.Binary;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public class CfwPackageConverterTests
{
    [Fact]
    public void Convert_FakeSignsAndPatchesEveryExecutable_WhilePreservingOtherFiles()
    {
        byte[] firstElf = WithFirmware(MinimalElf.Build(), 0x00446001);
        byte[] secondElf = WithFirmware(MinimalElf.Build(), 0x00421001);
        byte[] payload = Enumerable.Range(0, 8193).Select(i => (byte)(i * 13 + 7)).ToArray();
        byte[] package = new SyntheticPkgBuilder()
            .AddDirectory("USRDIR")
            .AddDirectory("USRDIR/UPDATE")
            .AddFile("USRDIR/EBOOT.BIN", firstElf)
            .AddFile("USRDIR/UPDATE/eboot.bin", secondElf)
            .AddFile("USRDIR/module.sprx", WithFirmware(MinimalElf.Build(), 0x00475001))
            .AddFile("USRDIR/DATA.BIN", payload)
            .Build();
        var keys = new InMemoryKeyProvider();
        using var source = new MemoryStream(package);
        using var destination = new MemoryStream();

        CfwConversionReport report = CfwPackageConverter.Convert(source, destination, keys,
            new CfwConversionOptions
            {
                FirmwareTarget = new CfwFirmwareTarget(4, 0),
                TargetProfile = TargetCompatibilityProfile.Rpcs3,
                SourceName = "source.pkg",
                OutputName = "source-cfw.pkg",
            });

        Assert.Equal(3, report.Executables.Count);
        Assert.All(report.Executables, item => Assert.Contains("firmware patched", item.Action));
        Assert.Contains("4.46 → 4.00", report.Executables[0].FirmwareChange);
        Assert.Contains("RPCS3 emulator", report.ToText());

        destination.Position = 0;
        var convertedInfo = PkgReader.Read(destination, keys);
        foreach (var entry in convertedInfo.Entries.Where(entry =>
                     entry.Name.EndsWith("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
                     entry.Name.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] self = PkgReader.ExtractEntryBytes(destination, convertedInfo.Header, entry, keys);
            SelfInfo selfInfo = SelfReader.ParseInfo(new MemoryStream(self));
            Assert.True(selfInfo.IsLikelyFakeSigned);
            byte[] elf = SelfDecryptor.Decrypt(self).Elf;
            Assert.Equal("4.00", EbootPatcher.FindSdkVersion(elf)?.Display);
        }

        var dataEntry = convertedInfo.Entries.Single(entry => entry.Name == "USRDIR/DATA.BIN");
        Assert.Equal(payload, PkgReader.ExtractEntryBytes(destination, convertedInfo.Header, dataEntry, keys));
    }

    [Fact]
    public void Convert_AlreadyFakeSignedWithoutPatch_IsUnchanged()
    {
        byte[] fakeSelf = SelfBuilder.MakeFakeSelf(MinimalElf.Build());
        byte[] package = new SyntheticPkgBuilder().AddFile("USRDIR/EBOOT.BIN", fakeSelf).Build();
        var keys = new InMemoryKeyProvider();
        using var source = new MemoryStream(package);
        using var destination = new MemoryStream();

        CfwConversionReport report = CfwPackageConverter.Convert(source, destination, keys,
            new CfwConversionOptions { TargetProfile = TargetCompatibilityProfile.Rpcs3 });

        Assert.Equal("already fake-signed; unchanged", Assert.Single(report.Executables).Action);
        destination.Position = 0;
        PkgInfo info = PkgReader.Read(destination, keys);
        var entry = info.Entries.Single(item => item.Name == "USRDIR/EBOOT.BIN");
        Assert.Equal(fakeSelf, PkgReader.ExtractEntryBytes(destination, info.Header, entry, keys));
    }

    [Fact]
    public void Convert_FirmwareTargetNeverRaisesLowerRequirement()
    {
        byte[] package = new SyntheticPkgBuilder()
            .AddFile("USRDIR/EBOOT.BIN", WithFirmware(MinimalElf.Build(), 0x00340001))
            .Build();
        using var source = new MemoryStream(package);
        using var destination = new MemoryStream();
        var keys = new InMemoryKeyProvider();

        CfwConversionReport report = CfwPackageConverter.Convert(source, destination, keys,
            new CfwConversionOptions
            {
                FirmwareTarget = new CfwFirmwareTarget(4, 0),
                TargetProfile = TargetCompatibilityProfile.Rpcs3,
            });

        Assert.Contains("3.40 already at or below 4.00; unchanged", Assert.Single(report.Executables).FirmwareChange);
        destination.Position = 0;
        PkgInfo info = PkgReader.Read(destination, keys);
        PkgEntry entry = info.Entries.Single(item => item.Name == "USRDIR/EBOOT.BIN");
        byte[] self = PkgReader.ExtractEntryBytes(destination, info.Header, entry, keys);
        Assert.Equal("3.40", EbootPatcher.FindSdkVersion(SelfDecryptor.Decrypt(self).Elf)?.Display);
    }

    [Fact]
    public void Convert_NoEboot_ThrowsBeforeWriting()
    {
        byte[] package = new SyntheticPkgBuilder().AddFile("USRDIR/DATA.BIN", new byte[32]).Build();
        using var source = new MemoryStream(package);
        using var destination = new MemoryStream();

        var exception = Assert.Throws<PkgFormatException>(() =>
            CfwPackageConverter.Convert(source, destination, new InMemoryKeyProvider(),
                new CfwConversionOptions { TargetProfile = TargetCompatibilityProfile.Rpcs3 }));

        Assert.Contains("no EBOOT.BIN", exception.Message);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void Convert_PspPackage_IsRejected()
    {
        byte[] package = new SyntheticPkgBuilder { Psp = true }
            .AddFile("USRDIR/EBOOT.BIN", MinimalElf.Build())
            .Build();
        using var source = new MemoryStream(package);
        using var destination = new MemoryStream();

        var exception = Assert.Throws<PkgFormatException>(() =>
            CfwPackageConverter.Convert(source, destination, new InMemoryKeyProvider()));

        Assert.Contains("PS3 packages only", exception.Message);
    }

    private static byte[] WithFirmware(byte[] elf, uint version)
    {
        int offset = elf.Length;
        Array.Resize(ref elf, elf.Length + 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(offset + 0x00), 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(offset + 0x04), 0x13BCC5F6);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(offset + 0x0C), version);
        return elf;
    }
}
