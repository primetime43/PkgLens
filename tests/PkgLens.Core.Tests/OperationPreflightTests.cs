using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public sealed class OperationPreflightTests
{
    private const string ContentId = "UP0001-NPUB30910_00-PREFLIGHTTEST0001";

    [Fact]
    public void PspExport_ValidPackagePassesAndReportsExpectedSize()
    {
        using var directory = new TempDirectory();
        var builder = new SyntheticPkgBuilder
        {
            Psp = true,
            PspKeyType = 1,
            Finalization = PkgFinalization.Retail,
        };
        byte[] pbp = new byte[12345];
        builder.AddFile("USRDIR/CONTENT/EBOOT.PBP", pbp);
        string input = Write(directory, "game.pkg", builder.Build());

        OperationPreflightReport report = OperationPreflight.PspExport(input,
            Path.Combine(directory.Path, "EBOOT.PBP"), PspExportFormat.Pbp, new FileKeyProvider());

        Assert.True(report.CanProceed);
        Assert.Equal(pbp.LongLength, report.ExpectedOutputBytes);
        Assert.Contains(report.Checks, check => check.Name == "Package type" &&
            check.Status == PreflightCheckStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "Disk space");
    }

    [Fact]
    public void VitaDlcWithoutWorkBin_WarnsButCanProceed()
    {
        using var directory = new TempDirectory();
        var builder = new SyntheticPkgBuilder
        {
            Psp = true,
            PspKeyType = 3,
            Finalization = PkgFinalization.Retail,
        };
        builder.AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.VitaDlc);
        builder.AddMetadataU32((uint)PkgMetadataId.DrmType, (uint)PkgDrmType.Local);
        builder.AddFile("sce_sys/package/body.bin", new byte[4096]);
        string input = Write(directory, "dlc.pkg", builder.Build());

        OperationPreflightReport report = OperationPreflight.VitaExport(input, directory.Path,
            new FileKeyProvider(), workBinProvided: false);

        Assert.True(report.CanProceed);
        Assert.Contains(report.Checks, check => check.Name == "License material" &&
            check.Status == PreflightCheckStatus.Warning);
        Assert.Contains("addcont", report.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LicensedSelf_MissingRapBlocksThenInstalledRapPasses()
    {
        using var directory = new TempDirectory();
        using var raps = new TempDirectory();
        string input = Write(directory, "EBOOT.BIN", new SyntheticSelfBuilder
        {
            KeyRevision = 0x0004,
            NpdrmContentId = ContentId,
            NpdrmLicenseType = 2,
        }.Build());
        string output = Path.Combine(directory.Path, "EBOOT.ELF");

        OperationPreflightReport missing = OperationPreflight.SelfDecrypt(input, output,
            null, raps.Path, thenFakeSign: false);
        Assert.False(missing.CanProceed);
        Assert.Contains(missing.Checks, check => check.Name == "License / RAP" &&
            check.Status == PreflightCheckStatus.Error);

        RapStore.Install(ContentId, new byte[16], raps.Path);
        OperationPreflightReport available = OperationPreflight.SelfDecrypt(input, output,
            null, raps.Path, thenFakeSign: false);
        Assert.True(available.CanProceed);
        Assert.Contains(available.Checks, check => check.Name == "License / RAP" &&
            check.Status == PreflightCheckStatus.Pass);
    }

    [Fact]
    public void LicensedEdatWithoutRapIsBlocked()
    {
        using var directory = new TempDirectory();
        using var raps = new TempDirectory();
        string input = Write(directory, "content.edat", BuildEdatHeader(ContentId, license: 2));

        OperationPreflightReport report = OperationPreflight.DataDecrypt(input,
            Path.Combine(directory.Path, "content.bin"), null, raps.Path);

        Assert.False(report.CanProceed);
        Assert.Equal(10, report.ExpectedOutputBytes);
        Assert.Contains(report.Checks, check => check.Name == "License / RAP" &&
            check.Status == PreflightCheckStatus.Error);
    }

    [Fact]
    public void CfwConversion_MissingRapBlocksAndReportsExecutable()
    {
        using var directory = new TempDirectory();
        using var raps = new TempDirectory();
        byte[] self = new SyntheticSelfBuilder
        {
            KeyRevision = 0x0004,
            NpdrmContentId = ContentId,
            NpdrmLicenseType = 2,
        }.Build();
        var builder = new SyntheticPkgBuilder
        {
            Finalization = PkgFinalization.Retail,
            RetailAesKey = BundledKeys.Ps3GpkgAesKey,
        };
        builder.AddFile("USRDIR/EBOOT.BIN", self);
        string input = Write(directory, "game.pkg", builder.Build());

        OperationPreflightReport report = OperationPreflight.CfwConversion(input,
            Path.Combine(directory.Path, "game-cfw.pkg"), new FileKeyProvider(), raps.Path);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Checks, check => check.Name == "Convertible executables" &&
            check.Status == PreflightCheckStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "RAP / licenses" &&
            check.Status == PreflightCheckStatus.Error);
    }

    [Fact]
    public void SameInputAndOutputPathIsBlocked()
    {
        using var directory = new TempDirectory();
        string input = Write(directory, "EBOOT.BIN", new SyntheticSelfBuilder
        {
            KeyRevision = 0x8000,
            NpdrmContentId = null,
            ProgramType = 4,
        }.Build());

        OperationPreflightReport report = OperationPreflight.SelfDecrypt(input, input,
            null, null, thenFakeSign: false);

        Assert.False(report.CanProceed);
        Assert.Contains(report.Checks, check => check.Name == "Output path" &&
            check.Status == PreflightCheckStatus.Error);
    }

    private static byte[] BuildEdatHeader(string contentId, int license)
    {
        var bytes = new byte[0x90];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x00), 0x4E504400);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x04), 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x08), (uint)license);
        Encoding.ASCII.GetBytes(contentId).CopyTo(bytes.AsSpan(0x10));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x84), 0x4000);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0x88), 10);
        return bytes;
    }

    private static string Write(TempDirectory directory, string name, byte[] data)
    {
        string path = Path.Combine(directory.Path, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"pkglens-preflight-tests-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
