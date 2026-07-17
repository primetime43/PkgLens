using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public sealed class KeyLicenseAuditTests
{
    private const string ContentId = "UP0001-NPUB30910_00-AUDITEXAMPLE00001";

    [Fact]
    public void FolderAudit_ReportsMissingThenAvailableRap()
    {
        using var root = new TempDirectory();
        using var raps = new TempDirectory();
        string eboot = Path.Combine(root.Path, "EBOOT.BIN");
        File.WriteAllBytes(eboot, new SyntheticSelfBuilder
        {
            KeyRevision = 0x0004,
            NpdrmContentId = ContentId,
            NpdrmLicenseType = 2,
        }.Build());

        var options = new KeyLicenseAuditOptions { RapDirectory = raps.Path };
        KeyLicenseAuditItem missing = Assert.Single(KeyLicenseAudit.Inspect(root.Path, options: options).Items);
        Assert.Equal(KeyLicenseAuditKind.Self, missing.Kind);
        Assert.Equal((ushort)0x0004, missing.SelfRevision);
        Assert.Equal("Local", missing.LicenseType);
        Assert.Equal(KeyLicenseAuditStatus.Available, missing.KeyStatus);
        Assert.Equal(KeyLicenseAuditStatus.Missing, missing.RapStatus);

        string rapPath = RapStore.Install(ContentId, Enumerable.Range(0, 16).Select(value => (byte)value).ToArray(), raps.Path);
        KeyLicenseAuditItem available = Assert.Single(KeyLicenseAudit.Inspect(root.Path, options: options).Items);
        Assert.Equal(KeyLicenseAuditStatus.Available, available.RapStatus);
        Assert.Equal(rapPath, available.RapPath);
    }

    [Fact]
    public void FolderAudit_ReportsUnsupportedSelfRevision()
    {
        using var root = new TempDirectory();
        File.WriteAllBytes(Path.Combine(root.Path, "module.sprx"), new SyntheticSelfBuilder
        {
            KeyRevision = 0x7777,
            NpdrmContentId = null,
            ProgramType = 4,
        }.Build());

        KeyLicenseAuditItem item = Assert.Single(KeyLicenseAudit.Inspect(root.Path).Items);

        Assert.Equal(KeyLicenseAuditStatus.Unsupported, item.KeyStatus);
        Assert.Equal(KeyLicenseAuditStatus.Unsupported, item.EncryptionStatus);
        Assert.Equal(1, KeyLicenseAudit.Inspect(root.Path).UnsupportedCount);
    }

    [Fact]
    public void PackageAudit_ReportsPackageKeyAndNestedSelf()
    {
        using var root = new TempDirectory();
        byte[] self = new SyntheticSelfBuilder
        {
            KeyRevision = 0x0004,
            NpdrmContentId = ContentId,
            NpdrmLicenseType = 3,
        }.Build();
        var builder = new SyntheticPkgBuilder
        {
            Finalization = PkgFinalization.Retail,
            RetailAesKey = BundledKeys.Ps3GpkgAesKey,
        };
        builder.AddMetadataU32((uint)PkgMetadataId.DrmType, (uint)PkgDrmType.Free);
        builder.AddFile("USRDIR/EBOOT.BIN", self);
        string packagePath = Path.Combine(root.Path, "audit.pkg");
        File.WriteAllBytes(packagePath, builder.Build());

        KeyLicenseAuditReport report = KeyLicenseAudit.Inspect(packagePath, new FileKeyProvider(root.Path));

        Assert.Equal(2, report.Items.Count);
        KeyLicenseAuditItem package = Assert.Single(report.Items, item => item.Kind == KeyLicenseAuditKind.Package);
        Assert.Equal("Bundled standard PS3 retail package AES key", package.RequiredKey);
        Assert.Equal(KeyLicenseAuditStatus.Available, package.KeyStatus);
        Assert.Equal("Free", package.LicenseType);
        KeyLicenseAuditItem nested = Assert.Single(report.Items, item => item.Kind == KeyLicenseAuditKind.Self);
        Assert.Contains("::USRDIR/EBOOT.BIN", nested.Source);
        Assert.Equal(ContentId, nested.ContentId);
        Assert.Equal(KeyLicenseAuditStatus.NotRequired, nested.RapStatus);
    }

    [Fact]
    public void PackageAudit_ReportsUnsupportedVitaKeyRevision()
    {
        using var root = new TempDirectory();
        byte[] package = new SyntheticPkgBuilder
        {
            Psp = true,
            PspKeyType = 2,
            Finalization = PkgFinalization.Retail,
        }.AddFile("test.bin", "x").Build();
        package[0xE7] = 7;
        string path = Path.Combine(root.Path, "unsupported.pkg");
        File.WriteAllBytes(path, package);

        KeyLicenseAuditItem item = Assert.Single(KeyLicenseAudit.Inspect(path).Items);

        Assert.Equal(KeyLicenseAuditKind.Package, item.Kind);
        Assert.Equal(KeyLicenseAuditStatus.Unsupported, item.KeyStatus);
        Assert.Equal(KeyLicenseAuditStatus.Unsupported, item.EncryptionStatus);
    }

    [Fact]
    public void JsonAndText_IncludeAuditSummary()
    {
        using var root = new TempDirectory();
        File.WriteAllBytes(Path.Combine(root.Path, "EBOOT.BIN"), new SyntheticSelfBuilder().Build());
        KeyLicenseAuditReport report = KeyLicenseAudit.Inspect(root.Path);

        Assert.Contains("\"selfCount\": 1", KeyLicenseAudit.ToJson(report));
        Assert.Contains("KEY / LICENSE AUDIT", KeyLicenseAudit.ToText(report));
        Assert.Contains("SELF revision: 0x0004", KeyLicenseAudit.ToText(report));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"pkglens-audit-tests-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
