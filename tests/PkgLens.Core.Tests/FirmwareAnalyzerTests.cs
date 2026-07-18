using System.Buffers.Binary;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public sealed class FirmwareAnalyzerTests
{
    [Fact]
    public void AnalyzeDirectory_ReportsEveryExecutableAndHighestFirmware()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "USRDIR"));
        File.WriteAllBytes(Path.Combine(directory.Path, "USRDIR", "EBOOT.BIN"),
            FakeSelf(0x00446001, 44600));
        File.WriteAllBytes(Path.Combine(directory.Path, "USRDIR", "module.sprx"),
            FakeSelf(0x00488001, 48800));
        File.WriteAllBytes(Path.Combine(directory.Path, "USRDIR", "ignored.bin"), new byte[32]);

        FirmwareAnalysisReport report = FirmwareAnalyzer.Analyze(directory.Path);

        Assert.Equal(2, report.Items.Count);
        Assert.Equal("4.88", report.HighestRequiredFirmware);
        Assert.Equal(2, report.PatchableCount);
        Assert.All(report.Items, item => Assert.True(item.CanPatch));
        Assert.Contains(report.Items, item => item.Path.EndsWith("module.sprx") && item.SdkFirmware == "4.88");
    }

    [Fact]
    public void AnalyzeFile_HeaderStillReportedWhenSdkMarkerIsMissing()
    {
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "EBOOT.BIN");
        File.WriteAllBytes(path, SelfBuilder.MakeFakeSelf(MinimalElf.Build(),
            new SelfBuilder.FakeSelfOptions { FirmwareVersion = 42100 }));

        FirmwareAnalysisItem item = Assert.Single(FirmwareAnalyzer.Analyze(path).Items);

        Assert.Equal("4.21", item.HeaderFirmware);
        Assert.Null(item.SdkFirmware);
        Assert.False(item.CanPatch);
        Assert.Contains("no sys_process_param", item.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalyzePackage_FindsEbootAndSprxEntries()
    {
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "game.pkg");
        byte[] package = new SyntheticPkgBuilder()
            .AddDirectory("USRDIR")
            .AddFile("USRDIR/EBOOT.BIN", FakeSelf(0x00460001, 46000))
            .AddFile("USRDIR/plugin.sprx", FakeSelf(0x00475001, 47500))
            .AddFile("USRDIR/data.bin", new byte[64])
            .Build();
        File.WriteAllBytes(path, package);

        FirmwareAnalysisReport report = FirmwareAnalyzer.Analyze(path, new InMemoryKeyProvider());

        Assert.Equal(2, report.Items.Count);
        Assert.Equal("4.75", report.HighestRequiredFirmware);
        Assert.Equal(2, report.PatchableCount);
    }

    [Fact]
    public void Analyze_RejectsNonpositiveMaximumExecutableSize()
    {
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "EBOOT.BIN");
        File.WriteAllBytes(path, new byte[4]);

        Assert.Throws<ArgumentOutOfRangeException>(() => FirmwareAnalyzer.Analyze(path,
            options: new FirmwareAnalysisOptions { MaximumExecutableBytes = 0 }));
    }

    [Fact]
    public void AnalyzePspPackage_ExplainsWhyPs3FirmwareAnalysisDoesNotApply()
    {
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "psp.pkg");
        byte[] package = new SyntheticPkgBuilder { Psp = true, PspKeyType = 1 }
            .AddFile("USRDIR/CONTENT/EBOOT.PBP", new byte[32])
            .Build();
        File.WriteAllBytes(path, package);

        FirmwareAnalysisReport report = FirmwareAnalyzer.Analyze(path, new FileKeyProvider());

        Assert.False(report.IsApplicable);
        Assert.Empty(report.Items);
        Assert.Contains("PSP package", report.Guidance);
        Assert.Contains("Export PSP package", report.Guidance);
    }

    private static byte[] FakeSelf(uint sdkVersion, ulong headerFirmware)
    {
        byte[] elf = MinimalElf.Build();
        int offset = elf.Length;
        Array.Resize(ref elf, elf.Length + 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(offset), 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(offset + 0x04), 0x13BCC5F6);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(offset + 0x0C), sdkVersion);
        return SelfBuilder.MakeFakeSelf(elf,
            new SelfBuilder.FakeSelfOptions { FirmwareVersion = headerFirmware });
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pkglens-firmware-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
