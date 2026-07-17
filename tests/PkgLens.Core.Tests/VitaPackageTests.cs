using System.Text;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using PkgLens.Core.Vita;
using Xunit;

namespace PkgLens.Core.Tests;

public class VitaPackageTests
{
    private const string ContentId = "UP0001-PCSE12345_00-TESTVITA00000001";

    [Theory]
    [InlineData(2, 0x15u, "gd", VitaPackageKind.App, "app/PCSE12345")]
    [InlineData(3, 0x15u, "gp", VitaPackageKind.Update, "patch/PCSE12345")]
    [InlineData(4, 0x16u, "ac", VitaPackageKind.Dlc, "addcont/PCSE12345/TESTVITA00000001")]
    [InlineData(2, 0x1Fu, "th", VitaPackageKind.Theme, "app/PCSE12345")]
    public void Inspect_ClassifiesVitaTypesAndKeyRevisions(byte revision, uint contentType,
        string category, VitaPackageKind expectedKind, string expectedRoot)
    {
        using var package = new MemoryStream(BuildPackage(revision, contentType, category));

        VitaPackageDetails details = VitaPackageExporter.Inspect(package, new FileKeyProvider());

        Assert.Equal(expectedKind, details.Kind);
        Assert.Equal(expectedRoot, details.RelativeRoot);
        Assert.Equal(revision, details.KeyRevision);
        Assert.Equal("PCSE12345", details.TitleId);
        Assert.Equal("Vita Test", details.Title);
        Assert.Equal("01.23", details.AppVersion);
        Assert.Equal("03.650", details.MinimumFirmware);
    }

    [Fact]
    public void Inspect_RejectsUnsupportedVitaKeyRevision()
    {
        var info = new PkgInfo
        {
            Header = new PkgHeader
            {
                Platform = PkgPlatform.PspPsVita,
                PspKeyType = 5,
            },
            Metadata = new PkgMetadata(Array.Empty<PkgMetadataEntry>()),
            IsDecrypted = true,
        };

        PkgFormatException error = Assert.Throws<PkgFormatException>(() => VitaPackageExporter.Inspect(info));
        Assert.Contains("2, 3, or 4", error.Message);
    }

    [Fact]
    public void Export_WritesAppTreePackageArtifactsRawBodyAndWorkBin()
    {
        byte[] pkg = BuildPackage(3, 0x15, "gd", includeBody: true);
        string directory = Path.Combine(Path.GetTempPath(), "pkglens-vita-" + Guid.NewGuid().ToString("N"));
        var workBin = new byte[512];
        Encoding.ASCII.GetBytes(ContentId).CopyTo(workBin, 0x10);
        try
        {
            using var package = new MemoryStream(pkg);
            PkgInfo info = PkgReader.Read(package, new FileKeyProvider());
            PkgEntry bodyEntry = info.Entries.Single(entry => entry.Name == "sce_sys/package/digs.bin");
            byte[] expectedRawBody = pkg.AsSpan(
                checked((int)(info.Header.DataOffset + bodyEntry.FileOffset)),
                checked((int)bodyEntry.FileSize)).ToArray();
            package.Position = 0;

            VitaExportResult result = VitaPackageExporter.Export(package, directory,
                new FileKeyProvider(), workBin);

            string root = Path.Combine(directory, "app", "PCSE12345");
            Assert.Equal(root, result.OutputRoot);
            Assert.Equal(VitaLicenseStatus.Included, result.LicenseStatus);
            Assert.Equal("Vita executable", File.ReadAllText(Path.Combine(root, "eboot.bin")));
            Assert.Equal(expectedRawBody, File.ReadAllBytes(Path.Combine(root, "sce_sys", "package", "body.bin")));
            Assert.Equal(workBin, File.ReadAllBytes(Path.Combine(root, "sce_sys", "package", "work.bin")));
            Assert.True(File.Exists(Path.Combine(root, "sce_sys", "package", "head.bin")));
            Assert.True(File.Exists(Path.Combine(root, "sce_sys", "package", "tail.bin")));
            Assert.Equal(768, new FileInfo(Path.Combine(root, "sce_sys", "package", "stat.bin")).Length);
            Assert.False(File.Exists(Path.Combine(root, "sce_sys", "package", "digs.bin")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Export_WithoutLicense_ReportsMissingInsteadOfPretendingContentIsLicensed()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pkglens-vita-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var package = new MemoryStream(BuildPackage(2, 0x16, "ac"));

            VitaExportResult result = VitaPackageExporter.Export(package, directory, new FileKeyProvider());

            Assert.Equal(VitaLicenseStatus.Missing, result.LicenseStatus);
            Assert.Contains(result.Warnings, warning => warning.Contains("work.bin", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Export_RejectsWorkBinForDifferentContentId()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pkglens-vita-" + Guid.NewGuid().ToString("N"));
        var workBin = new byte[512];
        Encoding.ASCII.GetBytes("UP0001-PCSE99999_00-WRONGCONTENT00001").CopyTo(workBin, 0x10);
        try
        {
            using var package = new MemoryStream(BuildPackage(2, 0x15, "gd"));
            Assert.Throws<PkgKeyException>(() => VitaPackageExporter.Export(
                package, directory, new FileKeyProvider(), workBin));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VitaMetadataIdsExposeItemAndSfoRanges()
    {
        var itemData = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(itemData, 0x20);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(itemData.AsSpan(4), 0x80);
        var metadata = new PkgMetadata(new[]
        {
            new PkgMetadataEntry { RawId = 0x0D, Data = itemData },
        });

        Assert.Equal(PkgMetadataId.PsVitaItemInfo, metadata.Entries[0].Id);
        Assert.Equal((0x20u, 0x80u), metadata.PsVitaItemRange);
    }

    private static byte[] BuildPackage(byte revision, uint contentType, string category, bool includeBody = false)
    {
        byte[] sfo = new SfoBuilder()
            .AddString("TITLE", "Vita Test")
            .AddString("TITLE_ID", "PCSE12345")
            .AddString("CONTENT_ID", ContentId)
            .AddString("CATEGORY", category)
            .AddString("APP_VER", "01.23")
            .AddString("PSP2_DISP_VER", "03.650")
            .Build();
        var builder = new SyntheticPkgBuilder
        {
            Psp = true,
            PspKeyType = revision,
            Finalization = PkgFinalization.Retail,
            ContentId = ContentId,
        }
            .AddMetadataU32((uint)PkgMetadataId.ContentType, contentType)
            .AddMetadataU32((uint)PkgMetadataId.DrmType, (uint)PkgDrmType.Local)
            .AddDirectory("sce_sys")
            .AddFile("sce_sys/param.sfo", sfo)
            .AddFile("eboot.bin", Encoding.UTF8.GetBytes("Vita executable"));
        if (includeBody)
        {
            builder.AddDirectory("sce_sys/package")
                .AddFile("sce_sys/package/digs.bin", Enumerable.Range(0, 96).Select(value => (byte)value).ToArray());
        }
        return builder.Build();
    }
}
