using PkgLens.Cli;
using PkgLens.Core.Ps3.Npd;

namespace PkgLens.Core.Tests;

public sealed class RapStoreTests
{
    private const string ContentId = "UP0001-NPUB30910_00-EXAMPLE000000001";

    [Fact]
    public void InstallFindListRemove_RoundTripsWithoutExposingBytes()
    {
        using var directory = new TempDirectory();
        byte[] rap = Enumerable.Range(0, 16).Select(index => (byte)(index * 7)).ToArray();

        string path = RapStore.Install(ContentId, rap, directory.Path, overwrite: false);

        Assert.Equal(rap, RapStore.Find(ContentId, directory.Path));
        RapStoreEntry entry = Assert.Single(RapStore.List(directory.Path));
        Assert.Equal(ContentId, entry.ContentId);
        Assert.Equal(path, entry.Path);
        Assert.Equal(16, entry.Size);
        Assert.True(entry.IsValid);
        Assert.Null(entry.Error);
        Assert.True(RapStore.Remove(ContentId, directory.Path));
        Assert.Null(RapStore.Find(ContentId, directory.Path));
        Assert.False(RapStore.Remove(ContentId, directory.Path));
    }

    [Fact]
    public void ContentId_PathTraversal_IsRejected()
    {
        using var directory = new TempDirectory();

        Assert.Throws<ArgumentException>(() => RapStore.Install("../outside", new byte[16], directory.Path));
        Assert.Throws<ArgumentException>(() => RapStore.Find("folder\\outside", directory.Path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public void List_ReportsInvalidRapLength()
    {
        using var directory = new TempDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, ContentId + ".rap"), new byte[15]);

        RapStoreEntry entry = Assert.Single(RapStore.List(directory.Path));

        Assert.False(entry.IsValid);
        Assert.Contains("16 bytes", entry.Error);
        Assert.Null(RapStore.Find(ContentId, directory.Path));
    }

    [Fact]
    public void RemoveEntry_CleansUpInvalidFilename()
    {
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "bad.name.rap");
        File.WriteAllBytes(path, new byte[15]);
        RapStoreEntry entry = Assert.Single(RapStore.List(directory.Path));

        Assert.False(entry.IsValid);
        Assert.True(RapStore.RemoveEntry(entry, directory.Path + Path.DirectorySeparatorChar));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Resolve_PrefersKlicenseeThenExplicitRapThenLibrary()
    {
        using var directory = new TempDirectory();
        byte[] storedRap = Enumerable.Repeat((byte)0x11, 16).ToArray();
        byte[] explicitRap = Enumerable.Repeat((byte)0x22, 16).ToArray();
        byte[] explicitKlicensee = Enumerable.Repeat((byte)0x33, 16).ToArray();
        RapStore.Install(ContentId, storedRap, directory.Path);
        string rapPath = Path.Combine(directory.Path, "explicit.rap");
        string klicenseeDatabase = Path.Combine(directory.Path, "klicensees.json");
        File.WriteAllBytes(rapPath, explicitRap);

        NpKlic.Resolution klic = NpKlic.Resolve(Convert.ToHexString(explicitKlicensee), rapPath,
            ContentId, directory.Path, klicenseeDatabasePath: klicenseeDatabase);
        NpKlic.Resolution file = NpKlic.Resolve(null, rapPath, ContentId, directory.Path,
            klicenseeDatabasePath: klicenseeDatabase);
        NpKlic.Resolution store = NpKlic.Resolve(null, null, ContentId, directory.Path,
            klicenseeDatabasePath: klicenseeDatabase);

        Assert.Equal(explicitKlicensee, klic.Klicensee);
        Assert.Equal("klicensee", klic.Source);
        Assert.Equal(NpdKeys.RapToKlicensee(explicitRap), file.Klicensee);
        Assert.Equal("rap-file", file.Source);
        Assert.Equal(NpdKeys.RapToKlicensee(storedRap), store.Klicensee);
        Assert.Equal("rap-store", store.Source);
    }

    [Fact]
    public void Resolve_UsesKlicenseeLibraryBeforeRapLibrary()
    {
        using var directory = new TempDirectory();
        byte[] storedRap = Enumerable.Repeat((byte)0x11, 16).ToArray();
        byte[] storedKlicensee = Enumerable.Repeat((byte)0x44, 16).ToArray();
        string database = Path.Combine(directory.Path, "klicensees.json");
        RapStore.Install(ContentId, storedRap, directory.Path);
        KlicenseeStore.Install(ContentId, "module.self", 2, storedKlicensee, "test", database);

        NpKlic.Resolution resolution = NpKlic.Resolve(null, null, ContentId, directory.Path,
            "module.self", 2, database);

        Assert.Equal(storedKlicensee, resolution.Klicensee);
        Assert.Equal("klicensee-store", resolution.Source);
        Assert.Equal(database, resolution.RapPath);
    }

    [Fact]
    public void Resolve_CorruptKlicenseeDatabaseStillFallsBackToRapLibrary()
    {
        using var directory = new TempDirectory();
        byte[] storedRap = Enumerable.Repeat((byte)0x55, 16).ToArray();
        string database = Path.Combine(directory.Path, "klicensees.json");
        File.WriteAllText(database, "{broken");
        RapStore.Install(ContentId, storedRap, directory.Path);

        NpKlic.Resolution resolution = NpKlic.Resolve(null, null, ContentId, directory.Path,
            "module.self", 2, database);

        Assert.Equal(NpdKeys.RapToKlicensee(storedRap), resolution.Klicensee);
        Assert.Equal("rap-store", resolution.Source);
    }

    [Fact]
    public void Discovery_FindsMatchingRapsBesideInputAndInSelectedFolders()
    {
        using var nearby = new TempDirectory();
        using var selected = new TempDirectory();
        string input = Path.Combine(nearby.Path, "game.pkg");
        File.WriteAllBytes(input, Array.Empty<byte>());
        string nearbyRap = Path.Combine(nearby.Path, ContentId.ToLowerInvariant() + ".RAP");
        const string otherContentId = "EP0002-NPEB00001_00-OTHERCONTENT0001";
        string selectedRap = Path.Combine(selected.Path, otherContentId + ".rap");
        File.WriteAllBytes(nearbyRap, new byte[16]);
        File.WriteAllBytes(selectedRap, new byte[16]);

        IReadOnlyList<RapDiscoveryCandidate> found = RapDiscovery.Find(
            new[] { ContentId, otherContentId }, input, new[] { selected.Path });

        Assert.Equal(2, found.Count);
        Assert.All(found, candidate => Assert.True(candidate.IsValid));
        Assert.Contains(found, candidate => candidate.ContentId == ContentId && candidate.Path == nearbyRap);
        Assert.Contains(found, candidate => candidate.ContentId == otherContentId && candidate.Path == selectedRap);
    }

    [Fact]
    public void Discovery_ReportsInvalidLengthAndIgnoresUnrelatedFiles()
    {
        using var directory = new TempDirectory();
        string invalid = Path.Combine(directory.Path, ContentId + ".rap");
        File.WriteAllBytes(invalid, new byte[15]);
        File.WriteAllBytes(Path.Combine(directory.Path, "UP0000-UNRELATED_00-NOTREQUESTED00001.rap"), new byte[16]);

        RapDiscoveryCandidate candidate = Assert.Single(
            RapDiscovery.Find(new[] { ContentId }, directory.Path));

        Assert.False(candidate.IsValid);
        Assert.Equal(15, candidate.Size);
        Assert.Contains("16 bytes", candidate.Error);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pkglens-rap-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
