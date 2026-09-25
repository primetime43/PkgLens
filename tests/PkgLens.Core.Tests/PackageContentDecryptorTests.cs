using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;

namespace PkgLens.Core.Tests;

public sealed class PackageContentDecryptorTests
{
    private const string ContentId = "UP0001-NPUB12345_00-TESTCONTENT00001";
    private static readonly byte[] Rap = Enumerable.Range(1, 16).Select(n => (byte)n).ToArray();
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("The decrypted file contents.");

    [Fact]
    public void Export_MixedPackage_DecryptsInnerFilesAndRetainsOrdinaryFiles()
    {
        using var fixture = new Fixture();
        RapStore.Install(ContentId, Rap, fixture.Raps);
        byte[] elf = MinimalElf.Build();
        byte[] encrypted = EdatBuilder.BuildLicensed(Plain, ContentId, "data.edat", NpdKeys.RapToKlicensee(Rap));
        byte[] pkg = new SyntheticPkgBuilder().AddDirectory("USRDIR/empty")
            .AddFile("USRDIR/EBOOT.BIN", SelfBuilder.MakeFakeSelf(elf))
            .AddFile("USRDIR/data.edat", encrypted).AddFile("readme.txt", "hello")
            .AddFile("decryption-report.json", "package file with same name as report").Build();
        var report = fixture.Export(pkg);
        Assert.Equal(2, report.DecryptedCount);
        Assert.Equal(2, report.ExtractedCount);
        Assert.Equal(0, report.AttentionCount);
        Assert.Equal(elf, fixture.Read("USRDIR/EBOOT.BIN"));
        Assert.Equal(Plain, fixture.Read("USRDIR/data.edat"));
        Assert.Equal("hello", Encoding.UTF8.GetString(fixture.Read("readme.txt")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Output, "files/USRDIR/empty")));
        Assert.Contains("package file", Encoding.UTF8.GetString(fixture.Read("decryption-report.json")));
        string json = File.ReadAllText(Path.Combine(fixture.Output, "decryption-report.json"));
        Assert.Contains("Decrypted", json);
        Assert.DoesNotContain(Convert.ToHexString(NpdKeys.RapToKlicensee(Rap)), json, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(fixture.Output, "decryption-report.txt")));
    }

    [Fact]
    public void MissingLicense_AndCorruptFile_AreReportedAndPreserved_WhileOtherFilesComplete()
    {
        using var fixture = new Fixture();
        byte[] encrypted = EdatBuilder.BuildLicensed(Plain, ContentId, "locked.edat", NpdKeys.RapToKlicensee(Rap));
        byte[] corrupt = [0x53, 0x43, 0x45, 0x00];
        byte[] pkg = new SyntheticPkgBuilder().AddFile("locked.edat", encrypted)
            .AddFile("broken.self", corrupt).AddFile("last.txt", "still processed").Build();
        var report = fixture.Export(pkg);
        Assert.Equal(ContentDecryptStatus.MissingKey, report.Items[0].Status);
        Assert.Equal(ContentId, report.Items[0].ContentId);
        Assert.Equal(ContentDecryptStatus.Failed, report.Items[1].Status);
        Assert.Equal(ContentDecryptStatus.Extracted, report.Items[2].Status);
        Assert.Equal(encrypted, fixture.Read("locked.edat"));
        Assert.Equal(corrupt, fixture.Read("broken.self"));
        Assert.Equal(2, report.AttentionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongKeyOrCorruptCiphertext_NeverPublishesPartialPlaintext(bool corruptCiphertext)
    {
        using var fixture = new Fixture();
        byte[] encrypted = EdatBuilder.BuildLicensed(Plain, ContentId, "data.edat", NpdKeys.RapToKlicensee(Rap));
        if (corruptCiphertext) encrypted[0x110] ^= 0x80;
        RapStore.Install(ContentId, corruptCiphertext ? Rap : new byte[16], fixture.Raps);
        var report = fixture.Export(new SyntheticPkgBuilder().AddFile("data.edat", encrypted).Build());
        Assert.NotEqual(ContentDecryptStatus.Decrypted, report.Items.Single().Status);
        Assert.Contains(corruptCiphertext ? "integrity" : "RAP/klicensee", report.Items.Single().Details);
        Assert.Equal(encrypted, fixture.Read("data.edat"));
        Assert.False(File.Exists(Path.Combine(fixture.Output, "decrypt.tmp")));
    }

    [Fact]
    public void LocalKlicenseeAndFreeLicense_WorkWithoutRap()
    {
        using var fixture = new Fixture();
        byte[] key = NpdKeys.RapToKlicensee(Rap);
        KlicenseeStore.Install(ContentId, "custom.edat", 2, key, "test", fixture.Database);
        byte[] free = EdatBuilder.BuildLicensed(Plain, ContentId, "free.edat", NpdKeys.KlicFree);
        BinaryPrimitives.WriteInt32BigEndian(free.AsSpan(8), 3);
        AesCmac.Compute(NpdKeys.KlicFree, free.AsSpan(0, 0xA0)).CopyTo(free, 0xA0);
        var report = fixture.Export(new SyntheticPkgBuilder()
            .AddFile("custom.edat", EdatBuilder.BuildLicensed(Plain, ContentId, "custom.edat", key))
            .AddFile("free.edat", free).Build());
        Assert.Equal(2, report.DecryptedCount);
        Assert.Equal(Plain, fixture.Read("free.edat"));
        Assert.Equal(Plain, fixture.Read("custom.edat"));
    }

    [Fact]
    public void UnsupportedFormats_AndBufferLimit_PreserveOriginals()
    {
        using var fixture = new Fixture();
        byte[] fusePgd = new byte[0x90];
        "\0PGD"u8.CopyTo(fusePgd);
        BinaryPrimitives.WriteUInt32LittleEndian(fusePgd.AsSpan(8), 2);
        byte[] self = SelfBuilder.MakeFakeSelf(MinimalElf.Build());
        var report = fixture.Export(new SyntheticPkgBuilder().AddFile("manual.pgd", fusePgd)
            .AddFile("EBOOT.PBP", "\0PBP container").AddFile("large.self", self).Build(), maxBuffered: 256);
        Assert.All(report.Items, item => Assert.Equal(ContentDecryptStatus.Unsupported, item.Status));
        Assert.Contains("Fuse-bound", report.Items[0].Details);
        Assert.Equal(self, fixture.Read("large.self"));
    }

    [Fact]
    public void PendingReplacements_AreExportedAndDecrypted_WithoutChangingSourceOrEdits()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "source.pkg");
        byte[] original = new SyntheticPkgBuilder().AddFile("readme.txt", "original").Build();
        File.WriteAllBytes(source, original);
        using var service = PackageOperationService.Open(source, new InMemoryKeyProvider());
        byte[] elf = MinimalElf.Build();
        service.ReplaceEntry(service.Info.Entries.Single(), SelfBuilder.MakeFakeSelf(elf));
        var report = service.DecryptContents(fixture.Output, fixture.Options());
        Assert.Equal(elf, fixture.Read("readme.txt")); // Detect by bytes, not extension.
        Assert.True(report.Items.Single().IncludedPendingEdit);
        Assert.True(service.HasPendingChanges);
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void Cancellation_RemovesStagingAndDoesNotPublishOutput()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.Message.StartsWith("Processed")) cancellation.Cancel(); });
        byte[] pkg = new SyntheticPkgBuilder().AddFile("first.txt", "first").AddFile("second.txt", "second").Build();
        Assert.Throws<OperationCanceledException>(() => fixture.Export(pkg, token: cancellation.Token, progress: progress));
        Assert.False(Directory.Exists(fixture.Output));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".pkglens-decrypt-*"));
    }

    [Fact]
    public void UnsupportedSelfRevision_AndMissingSelfLicense_HaveDistinctResults()
    {
        using var fixture = new Fixture();
        byte[] unsupported = new SyntheticSelfBuilder { KeyRevision = 0x7FFF }.Build();
        byte[] licensed = new SyntheticSelfBuilder { NpdrmContentId = ContentId, NpdrmLicenseType = 2 }.Build();
        var report = fixture.Export(new SyntheticPkgBuilder().AddFile("unknown.self", unsupported)
            .AddFile("EBOOT.BIN", licensed).Build());
        Assert.Equal(ContentDecryptStatus.Unsupported, report.Items[0].Status);
        Assert.Equal(ContentDecryptStatus.MissingKey, report.Items[1].Status);
        Assert.Equal(licensed, fixture.Read("EBOOT.BIN"));
    }

    [Fact]
    public void VitaFiles_AreNeverMistakenForDecryptedPs3Content()
    {
        using var fixture = new Fixture();
        var report = fixture.Export(new SyntheticPkgBuilder { Psp = true, PspKeyType = 2, Finalization = PkgLens.Core.Shared.Models.PkgFinalization.Retail }
            .AddFile("eboot.bin", "inner protected payload").Build());
        Assert.Equal(ContentDecryptStatus.Unsupported, report.Items.Single().Status);
        Assert.Contains("PFS/SELF", report.Items.Single().Details);
        Assert.Equal(0, report.DecryptedCount);
    }

    [Fact]
    public void CaseCollisions_AreRejectedWithoutLosingEitherFile()
    {
        using var fixture = new Fixture();
        Assert.Throws<PkgFormatException>(() => fixture.Export(new SyntheticPkgBuilder()
            .AddFile("data.txt", "first").AddFile("DATA.TXT", "second").Build()));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public void ExistingDestination_IsNeverOverwritten()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Output);
        string sentinel = Path.Combine(fixture.Output, "existing.txt");
        File.WriteAllText(sentinel, "keep");
        Assert.Throws<IOException>(() => fixture.Export(new SyntheticPkgBuilder().AddFile("file.txt", "data").Build()));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("file:stream")]
    [InlineData("NUL.txt")]
    [InlineData("file.txt.")]
    public void UnsafePaths_AreRejectedBeforeExport(string name)
    {
        using var fixture = new Fixture();
        Assert.Throws<PkgFormatException>(() => fixture.Export(new SyntheticPkgBuilder().AddFile(name, "data").Build()));
        Assert.False(Directory.Exists(fixture.Output));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".pkglens-decrypt-*"));
    }

    private sealed class InlineProgress(Action<ContentDecryptProgress> report) : IProgress<ContentDecryptProgress>
    {
        public void Report(ContentDecryptProgress value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pkglens-content-test-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "export");
        public string Raps => Path.Combine(Root, "raps");
        public string Database => Path.Combine(Root, "keys.json");
        public Fixture() => Directory.CreateDirectory(Root);
        public ContentDecryptOptions Options(long maxBuffered = 128 * 1024 * 1024) => new()
        {
            RapDirectory = Raps, KlicenseeDatabasePath = Database, MaxBufferedFileBytes = maxBuffered,
        };
        public ContentDecryptReport Export(byte[] pkg, long maxBuffered = 128 * 1024 * 1024,
            CancellationToken token = default, IProgress<ContentDecryptProgress>? progress = null)
        {
            using var input = new MemoryStream(pkg);
            var keys = new FileKeyProvider(Path.Combine(Root, "package-keys"));
            var info = PkgReader.Read(input, keys);
            return PackageContentDecryptor.Export(input, info, keys, Output, Options(maxBuffered),
                cancellationToken: token, progress: progress);
        }
        public byte[] Read(string relative) => File.ReadAllBytes(Path.Combine(Output, "files", relative));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
