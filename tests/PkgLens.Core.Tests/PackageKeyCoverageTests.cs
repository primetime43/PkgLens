using System.Buffers.Binary;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;

namespace PkgLens.Core.Tests;

public sealed class PackageKeyCoverageTests
{
    private const string ContentId = "UP0001-NPUB12345_00-COVERAGETEST0001";
    private static readonly byte[] Key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void MixedPackage_ReportsConfirmedMissingDamagedAndUnsupported_WithoutWritingFiles()
    {
        using var fixture = new Fixture();
        byte[] corrupt = Edat("broken.edat"); corrupt[0x120] ^= 1;
        byte[] debug = Edat("debug.edat");
        BinaryPrimitives.WriteUInt32BigEndian(debug.AsSpan(0x80), 0x80000000);
        using var package = fixture.Open(new SyntheticPkgBuilder()
            .AddFile("free.edat", Edat("free.edat"))
            .AddFile("asset.dat", Edat("asset.dat", sdat: true))
            .AddFile("locked.edat", Edat("locked.edat", license: 2, key: Key))
            .AddFile("broken.edat", corrupt).AddFile("debug.edat", debug)
            .AddFile("EBOOT.BIN", SelfBuilder.MakeFakeSelf(MinimalElf.Build()))
            .AddFile("already.elf", MinimalElf.Build())
            .AddFile("unknown.sprx", "unknown")
            .AddFile("EBOOT.PBP", "\0PBP container")
            .AddFile("notes.txt", "ordinary").Build());
        byte[] original = File.ReadAllBytes(fixture.Source);
        string[] before = Directory.GetFiles(fixture.Root);
        var report = PackageKeyCoverageService.Scan(package, fixture.Options);
        Assert.Equal(10, report.ScannedFiles);
        Assert.Equal(1, report.OrdinaryFiles);
        Assert.Equal(4, report.UsableCount);
        Assert.Equal(5, report.AttentionCount);
        Assert.Equal(KeyCoverageStatus.Verified, Find("free.edat").Status);
        Assert.Equal("SDAT", Find("asset.dat").Format);
        Assert.Equal(KeyCoverageStatus.Verified, Find("asset.dat").Status);
        Assert.Equal(KeyCoverageStatus.MissingKey, Find("locked.edat").Status);
        Assert.Equal(KeyCoverageStatus.Damaged, Find("broken.edat").Status);
        Assert.Equal(KeyCoverageStatus.NotChecked, Find("debug.edat").Status);
        Assert.Equal(KeyCoverageStatus.Opens, Find("EBOOT.BIN").Status);
        Assert.Equal(KeyCoverageStatus.NoKeyNeeded, Find("already.elf").Status);
        Assert.Equal(KeyCoverageStatus.Unsupported, Find("unknown.sprx").Status);
        Assert.Equal(KeyCoverageStatus.Unsupported, Find("EBOOT.PBP").Status);
        Assert.Equal(original, File.ReadAllBytes(fixture.Source));
        Assert.Equal(before, Directory.GetFiles(fixture.Root));
        Assert.DoesNotContain(Convert.ToHexString(Key), report.ToJson());
        Assert.Contains("Needs key", report.ToText());
        KeyCoverageItem Find(string path) => report.Items.Single(i => i.Path == path);
    }

    [Fact]
    public void PendingContentAndSavedFreeDeveloperKey_AgreeWithPackageExport()
    {
        using var fixture = new Fixture();
        KlicenseeStore.Install(ContentId, "data.edat", 3, Key, "discovery test", fixture.Database);
        using var package = fixture.Open(new SyntheticPkgBuilder().AddFile("USRDIR/data.edat", "original plain text").Build());
        byte[] original = File.ReadAllBytes(fixture.Source);
        var entry = package.Info.Entries.Single();
        package.ReplaceEntry(entry, Edat("data.edat", key: Key));
        var item = Assert.Single(PackageKeyCoverageService.Scan(package, fixture.Options).Items);
        Assert.True(item.PendingEdit);
        Assert.Equal(KeyCoverageStatus.Verified, item.Status);
        Assert.Contains("Local klicensee", item.KeySource);
        string output = Path.Combine(fixture.Root, "export");
        Assert.Equal(1, package.DecryptContents(output, fixture.Options).DecryptedCount);
        Assert.Equal("payload"u8.ToArray(), File.ReadAllBytes(Path.Combine(output, "files", "USRDIR", "data.edat")));
        Assert.Equal(original, File.ReadAllBytes(fixture.Source));
        Assert.True(package.HasPendingChanges);
    }

    [Fact]
    public void WrongInstalledKey_IsUnconfirmed_AndRescanAfterImportBecomesVerified()
    {
        using var fixture = new Fixture();
        byte[] key = NpdKeys.RapToKlicensee(Key);
        using var package = fixture.Open(new SyntheticPkgBuilder().AddFile("licensed.edat", Edat("licensed.edat", 2, key)).Build());
        RapStore.Install(ContentId, new byte[16], fixture.Options.RapDirectory);
        var first = Assert.Single(PackageKeyCoverageService.Scan(package, fixture.Options).Items);
        Assert.Equal(KeyCoverageStatus.KeyNotConfirmed, first.Status);
        Assert.Contains("RAP library", first.KeySource);
        RapStore.Install(ContentId, Key, fixture.Options.RapDirectory);
        var second = Assert.Single(PackageKeyCoverageService.Scan(package, fixture.Options).Items);
        Assert.Equal(KeyCoverageStatus.Verified, second.Status);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void BufferLimitAndFuseBoundPsp_AreExplicitlyUnverified()
    {
        using var fixture = new Fixture();
        byte[] pgd = new byte[0x90]; "\0PGD"u8.CopyTo(pgd);
        BinaryPrimitives.WriteUInt32LittleEndian(pgd.AsSpan(8), 2);
        using var package = fixture.Open(new SyntheticPkgBuilder().AddFile("large.edat", Edat("large.edat"))
            .AddFile("manual.pgd", pgd).Build());
        var report = PackageKeyCoverageService.Scan(package, new ContentDecryptOptions { MaxBufferedFileBytes = 256 });
        Assert.Equal(KeyCoverageStatus.NotChecked, report.Items[0].Status);
        Assert.Equal(KeyCoverageStatus.Unsupported, report.Items[1].Status);
        Assert.Equal(0, report.UsableCount);
    }

    [Fact]
    public void CancelledScan_DoesNotReturnPartialReportOrChangeEdits()
    {
        using var fixture = new Fixture();
        using var package = fixture.Open(new SyntheticPkgBuilder().AddFile("data.edat", Edat("data.edat")).Build());
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => PackageKeyCoverageService.Scan(package, fixture.Options,
            cancellation.Token, new CancelProgress(cancellation)));
        Assert.False(package.HasPendingChanges);
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public void FreeContentWithoutTitleMapping_StillChecksAndExportsWithStandardKey()
    {
        using var fixture = new Fixture();
        using var package = fixture.Open(new SyntheticPkgBuilder().AddFile("data.edat", Edat("data.edat", contentId: "CUSTOM-CONTENT")).Build());
        Assert.Equal(KeyCoverageStatus.Verified, Assert.Single(PackageKeyCoverageService.Scan(package, fixture.Options).Items).Status);
        Assert.Equal(1, package.DecryptContents(Path.Combine(fixture.Root, "export"), fixture.Options).DecryptedCount);
    }

    private static byte[] Edat(string name, int license = 3, byte[]? key = null, bool sdat = false, string contentId = ContentId)
    {
        using var output = new MemoryStream();
        EdatWriter.WriteVerified(new MemoryStream("payload"u8.ToArray()), output,
            new EdatWriteOptions { FileName = name, ContentId = contentId, License = license, IsSdat = sdat }, key);
        return output.ToArray();
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<ContentDecryptProgress>
    { public void Report(ContentDecryptProgress value) => cancellation.Cancel(); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pkglens-coverage-test-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source.pkg");
        public string Database => Path.Combine(Root, "keys.json");
        public ContentDecryptOptions Options => new() { RapDirectory = Path.Combine(Root, "raps"), KlicenseeDatabasePath = Database };
        public Fixture() => Directory.CreateDirectory(Root);
        public PackageOperationService Open(byte[] bytes) { File.WriteAllBytes(Source, bytes); return PackageOperationService.Open(Source, new InMemoryKeyProvider()); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
