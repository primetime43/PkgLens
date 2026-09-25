using System.Buffers.Binary;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Gui.Services;

namespace PkgLens.Core.Tests;

public sealed class EdatKeyValidationTests
{
    private const string ContentId = "UP0001-NPUB12345_00-EDATTEST00000001";
    private static readonly byte[] Key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData("raw")]
    [InlineData("selected RAP")]
    [InlineData("library")]
    [InlineData("local")]
    [InlineData("free")]
    [InlineData("custom free")]
    [InlineData("sdat")]
    public void CorrectKey_ReportsSourceAndChecksContentWithoutWritingPlaintext(string kind)
    {
        using var fixture = new Fixture();
        var selection = new EdatKeySelection();
        byte[] key = Key;
        string expectedSource;
        int license = 2;
        switch (kind)
        {
            case "raw": selection = new(Convert.ToHexString(Key)); expectedSource = "Entered raw"; break;
            case "selected RAP":
            case "library":
                RapStore.Install(ContentId, Key, fixture.Raps);
                key = NpdKeys.RapToKlicensee(Key);
                if (kind == "selected RAP") selection = new(RapPath: RapStore.PathFor(ContentId, fixture.Raps));
                expectedSource = kind == "library" ? "RAP library" : "Selected RAP";
                break;
            case "local":
                KlicenseeStore.Install(ContentId, "source.edat", 2, Key, "test", fixture.Database);
                expectedSource = "Local klicensee database";
                break;
            case "free": license = 3; key = NpdKeys.KlicFree; expectedSource = "Built-in free"; break;
            case "custom free": license = 3; selection = new(Convert.ToHexString(Key)); expectedSource = "Entered raw"; break;
            default: expectedSource = "SDAT header"; selection = new("ignored invalid key"); break;
        }
        fixture.Write(key, license, kind == "sdat");
        byte[] original = File.ReadAllBytes(fixture.Source);
        string[] before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories);
        var result = fixture.Check(selection);
        Assert.Equal(EdatKeyCheckStatus.Verified, result.Status);
        Assert.StartsWith(expectedSource, result.Source);
        Assert.Equal(ContentId, result.ContentId);
        Assert.DoesNotContain(Convert.ToHexString(Key), result.DisplayText);
        Assert.DoesNotContain(Convert.ToHexString(key), result.ToString());
        Assert.Equal(original, File.ReadAllBytes(fixture.Source));
        Assert.Equal(before, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
        // Processing uses exactly the same resolution as validation.
        using var decrypted = EdatWorkbenchService.Build(new(EdatWorkbenchOperation.Decrypt, fixture.Source,
            "plain.bin", new(), selection, new(), RapDirectory: fixture.Raps, KlicenseeDatabasePath: fixture.Database));
        Assert.Equal(Fixture.Plaintext, File.ReadAllBytes(decrypted.PlaintextPath));
    }

    [Fact]
    public void MissingKey_ExplainsRequiredContentLicense()
    {
        using var fixture = new Fixture();
        fixture.Write(Key);
        var result = fixture.Check(new());
        Assert.Equal(EdatKeyCheckStatus.MissingKey, result.Status);
        Assert.Contains(ContentId, result.Message);
        Assert.Contains("Select its RAP", result.Message);
        Assert.Null(result.Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongKey_DoesNotMislabelFreeEdatAsDefinitelyDamaged(bool free)
    {
        using var fixture = new Fixture();
        fixture.Write(Key, free ? 3 : 2);
        var result = fixture.Check(new(Convert.ToHexString(new byte[16])));
        Assert.Equal(EdatKeyCheckStatus.HeaderMismatch, result.Status);
        Assert.Contains("incorrect or the header damaged", result.Message);
    }

    [Theory]
    [InlineData(0x40, EdatKeyCheckStatus.HeaderMismatch)]
    [InlineData(0x100, EdatKeyCheckStatus.DamagedContent)]
    [InlineData(0x120, EdatKeyCheckStatus.DamagedContent)]
    public void Corruption_AfterAuthenticatedHeader_IsDistinguishedFromUnconfirmedKey(int offset, EdatKeyCheckStatus status)
    {
        using var fixture = new Fixture();
        fixture.Write(Key);
        byte[] bytes = File.ReadAllBytes(fixture.Source);
        bytes[offset] ^= 0x80;
        File.WriteAllBytes(fixture.Source, bytes);
        var result = fixture.Check(new(Convert.ToHexString(Key)));
        Assert.Equal(status, result.Status);
        if (status == EdatKeyCheckStatus.DamagedContent) Assert.Contains("key matches the header", result.Message);
    }

    [Fact]
    public void DebugData_DoesNotClaimAKeyMatch()
    {
        using var fixture = new Fixture();
        fixture.Write(Key);
        byte[] bytes = File.ReadAllBytes(fixture.Source);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x80), 0x80000000);
        File.WriteAllBytes(fixture.Source, bytes);
        Assert.Null(EdatFile.AuthenticateHeader(new MemoryStream(bytes), new byte[16]));
        Assert.Equal(EdatKeyCheckStatus.Unverifiable, fixture.Check(new(Convert.ToHexString(new byte[16]))).Status);
    }

    [Fact]
    public void InvalidSelectedOrInstalledRap_IsNotReportedAsMissing()
    {
        using var fixture = new Fixture();
        fixture.Write(Key);
        Directory.CreateDirectory(fixture.Raps);
        string path = RapStore.PathFor(ContentId, fixture.Raps);
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal(EdatKeyCheckStatus.InvalidKey, fixture.Check(new(RapPath: path)).Status);
        Assert.Equal(EdatKeyCheckStatus.InvalidKey, fixture.Check(new()).Status);
        Assert.Equal(EdatKeyCheckStatus.InvalidKey, fixture.Check(new("bad hex")).Status);
        Assert.Equal(EdatKeyCheckStatus.Unavailable, fixture.Check(new(RapPath: path + ".missing")).Status);
    }

    [Fact]
    public void ExplicitKeyTakesPrecedence_AndCatalogMatchAloneDoesNotMeanVerified()
    {
        using var fixture = new Fixture();
        fixture.Write(Key);
        KlicenseeStore.Install(ContentId, "source.edat", 2, new byte[16], "test", fixture.Database);
        Assert.Equal(EdatKeyCheckStatus.HeaderMismatch, fixture.Check(new()).Status);
        Assert.Equal(EdatKeyCheckStatus.Verified, fixture.Check(new(Convert.ToHexString(Key), "nonexistent.rap")).Status);
    }

    [Fact]
    public void BundledCatalog_ReportsConfirmedMapping()
    {
        using var fixture = new Fixture();
        const string id = "UP0002-BLUS30192_00-CODWAWMAPPACK105";
        var known = KnownKlicenseeStore.Find(id, "source.edat", 3);
        Assert.NotNull(known);
        fixture.Write(known.Klicensee, 3, contentId: id);
        var result = fixture.Check(new());
        Assert.Equal(EdatKeyCheckStatus.Verified, result.Status);
        Assert.Contains("Bundled klicensee catalog", result.Source);
        Assert.DoesNotContain(Convert.ToHexString(known.Klicensee), result.DisplayText);
    }

    [Fact]
    public void TruncationAndCancellation_DoNotReportSuccess()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Source, "NPD"u8.ToArray());
        Assert.Equal(EdatKeyCheckStatus.InvalidFile, fixture.Check(new()).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EdatKeyValidationService.Check(fixture.Source, new(), token: cancellation.Token));
    }

    private sealed class Fixture : IDisposable
    {
        public static byte[] Plaintext => Enumerable.Range(0, 2049).Select(i => (byte)(i * 7)).ToArray();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pkglens-keycheck-test-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source.edat");
        public string Raps => Path.Combine(Root, "raps");
        public string Database => Path.Combine(Root, "keys.json");
        public Fixture() => Directory.CreateDirectory(Root);
        public void Write(byte[] key, int license = 2, bool sdat = false, string contentId = ContentId)
        {
            using var output = new FileStream(Source, FileMode.Create, FileAccess.ReadWrite);
            EdatWriter.WriteVerified(new MemoryStream(Plaintext), output, new EdatWriteOptions
            { ContentId = contentId, FileName = "source.edat", License = license, IsSdat = sdat, BlockSize = 1024 }, key);
        }
        public EdatKeyCheckResult Check(EdatKeySelection selection) => EdatKeyValidationService.Check(Source, selection, Raps, Database);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
