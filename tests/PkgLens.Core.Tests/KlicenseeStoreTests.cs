using PkgLens.Core.Ps3.Npd;

namespace PkgLens.Core.Tests;

public sealed class KlicenseeStoreTests
{
    private const string ContentId = "UP0002-BLUS30192_00-CODWAWMAPPACK105";

    [Fact]
    public void InstallFindListRemove_RoundTripsWithoutListingRawKey()
    {
        using var directory = new TempDirectory();
        string database = Path.Combine(directory.Path, "klicensees.json");
        byte[] key = Convert.FromHexString("C8E0234D149E4549BC1B086FEFD7282A");

        KlicenseeStoreEntry installed = KlicenseeStore.Install(ContentId, "mp.self", 3, key,
            "successful test decrypt", database);
        KlicenseeResolution? resolution = KlicenseeStore.Find(ContentId, "mp.self", 3, database);
        KlicenseeStoreEntry listed = Assert.Single(KlicenseeStore.List(database));

        Assert.NotNull(resolution);
        Assert.Equal(key, resolution.Klicensee);
        Assert.Equal(installed, listed);
        Assert.Equal("BLUS30192", listed.TitleId);
        Assert.Equal(12, listed.Fingerprint.Length);
        Assert.DoesNotContain(Convert.ToHexString(key), listed.ToString());
        Assert.True(KlicenseeStore.Remove(listed.Id, database));
        Assert.Empty(KlicenseeStore.List(database));
        Assert.False(KlicenseeStore.Remove(listed.Id, database));
    }

    [Fact]
    public void Find_PrefersExactContentAndFileMappings()
    {
        using var directory = new TempDirectory();
        string database = Path.Combine(directory.Path, "klicensees.json");
        string imports = Path.Combine(directory.Path, "klics.txt");
        byte[] broad = Enumerable.Repeat((byte)0x11, 16).ToArray();
        byte[] exact = Enumerable.Repeat((byte)0x22, 16).ToArray();
        File.WriteAllText(imports, $"{Convert.ToHexString(broad)} BLUS30192 broad mapping");
        KlicenseeStore.ImportFile(imports, database);
        KlicenseeStore.Install(ContentId, "mp.self", 3, exact, "verified", database);

        KlicenseeResolution? exactResult = KlicenseeStore.Find(ContentId, "mp.self", 3, database);
        KlicenseeResolution? broadResult = KlicenseeStore.Find(ContentId, "other.self", 3, database);

        Assert.Equal(exact, exactResult?.Klicensee);
        Assert.Equal(broad, broadResult?.Klicensee);
    }

    [Fact]
    public void Find_RejectsAmbiguousTitleWideMappings()
    {
        using var directory = new TempDirectory();
        string database = Path.Combine(directory.Path, "klicensees.json");
        string imports = Path.Combine(directory.Path, "klics.txt");
        File.WriteAllLines(imports,
        [
            "11111111111111111111111111111111 BLUS30192 candidate one",
            "22222222222222222222222222222222 BLUS30192 candidate two",
        ]);

        KlicenseeStore.ImportFile(imports, database);

        Assert.Equal(2, KlicenseeStore.List(database).Count);
        Assert.Null(KlicenseeStore.Find(ContentId, "mp.self", 3, database));
    }

    [Fact]
    public void ImportAnnotated_MapsTitleAndContentIdsButSkipsRawPool()
    {
        using var directory = new TempDirectory();
        string database = Path.Combine(directory.Path, "klicensees.json");
        string imports = Path.Combine(directory.Path, "klics.txt");
        const string euContentId = "EP0002-BLES00354_00-CODWAWMAPPACK105";
        File.WriteAllLines(imports,
        [
            "C8E0234D149E4549BC1B086FEFD7282A BLUS30192 Call of Duty World at War",
            "00112233445566778899AABBCCDDEEFF " + euContentId,
            "FFEEDDCCBBAA99887766554433221100",
            "not a mapping",
        ]);

        KlicenseeImportResult result = KlicenseeStore.ImportFile(imports, database);

        Assert.Equal(2, result.Imported);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(Convert.FromHexString("C8E0234D149E4549BC1B086FEFD7282A"),
            KlicenseeStore.Find(ContentId, "mp.self", 3, database)?.Klicensee);
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"),
            KlicenseeStore.Find(euContentId, null, null, database)?.Klicensee);
    }

    [Fact]
    public void ImportLegacyIni_MapsLicenseSpecificKeysAndFilename()
    {
        using var directory = new TempDirectory();
        string database = Path.Combine(directory.Path, "klicensees.json");
        string imports = Path.Combine(directory.Path, "klicensee.ini");
        File.WriteAllText(imports, """
            [klicensee]
            productid=BLUS30192
            filename=mp.self
            key1=11111111111111111111111111111111
            key2=22222222222222222222222222222222
            key3=C8E0234D149E4549BC1B086FEFD7282A
            [/klicensee]
            """);

        KlicenseeImportResult result = KlicenseeStore.ImportFile(imports, database);

        Assert.Equal(3, result.Imported);
        Assert.Equal(3, KlicenseeStore.List(database).Count);
        Assert.Equal(Convert.FromHexString("C8E0234D149E4549BC1B086FEFD7282A"),
            KlicenseeStore.Find(ContentId, "mp.self", 3, database)?.Klicensee);
        Assert.Equal(Convert.FromHexString("11111111111111111111111111111111"),
            KlicenseeStore.Find(ContentId, "mp.self", 1, database)?.Klicensee);
    }

    [Fact]
    public void List_CorruptDatabase_ThrowsUsefulError()
    {
        using var directory = new TempDirectory();
        string database = Path.Combine(directory.Path, "klicensees.json");
        File.WriteAllText(database, "{broken");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => KlicenseeStore.List(database));

        Assert.Contains("valid JSON", error.Message);
    }

    [Fact]
    public void BundledCatalog_ResolvesWorldAtWarTitleKeyForUnlistedRegionContentId()
    {
        KlicenseeResolution? resolution = KnownKlicenseeStore.Find(
            "UP0002-BLUS30192_00-CODWAWMAPPACK105", "sp.self", 3);

        Assert.NotNull(resolution);
        Assert.Equal(Convert.FromHexString("C8E0234D149E4549BC1B086FEFD7282A"),
            resolution.Klicensee);
        Assert.Equal(KnownKlicenseeStore.DatabaseId, resolution.DatabasePath);
        Assert.Equal("bundled catalog", resolution.Entry.Source);
        Assert.True(KnownKlicenseeStore.Count > 500);
    }

    [Fact]
    public void BundledCatalog_ListExposesEveryMappingWithoutRawKeys()
    {
        IReadOnlyList<KlicenseeStoreEntry> entries = KnownKlicenseeStore.List();

        Assert.Equal(KnownKlicenseeStore.Count, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal("bundled catalog", entry.Source);
            Assert.Equal(12, entry.Fingerprint.Length);
            Assert.Equal(DateTimeOffset.UnixEpoch, entry.AddedUtc);
        });
        Assert.Contains(entries, entry => entry.TitleId == "BLUS30192");
        Assert.DoesNotContain("C8E0234D149E4549BC1B086FEFD7282A",
            string.Join(Environment.NewLine, entries));
    }

    [Fact]
    public void BundledCatalog_RejectsConflictingKeysInsteadOfGuessing()
    {
        KlicenseeResolution? resolution = KnownKlicenseeStore.Find(
            "UP0700-NPUB30932_00-NNKDLFULLGAMEPTA", "EBOOT.BIN", 3);

        Assert.Null(resolution);
    }

    [Fact]
    public void BundledCatalog_HonorsFilenameSpecificMappings()
    {
        const string contentId = "UP0001-NPUB30737_00-FARCRYPS30000000";

        Assert.NotNull(KnownKlicenseeStore.Find(contentId, "BinkPS3SPU.spu.self", 3));
        Assert.Null(KnownKlicenseeStore.Find(contentId, "EBOOT.BIN", 3));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pkglens-klicensee-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
