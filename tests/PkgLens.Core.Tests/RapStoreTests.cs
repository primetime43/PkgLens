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
        File.WriteAllBytes(rapPath, explicitRap);

        NpKlic.Resolution klic = NpKlic.Resolve(Convert.ToHexString(explicitKlicensee), rapPath, ContentId, directory.Path);
        NpKlic.Resolution file = NpKlic.Resolve(null, rapPath, ContentId, directory.Path);
        NpKlic.Resolution store = NpKlic.Resolve(null, null, ContentId, directory.Path);

        Assert.Equal(explicitKlicensee, klic.Klicensee);
        Assert.Equal("klicensee", klic.Source);
        Assert.Equal(NpdKeys.RapToKlicensee(explicitRap), file.Klicensee);
        Assert.Equal("rap-file", file.Source);
        Assert.Equal(NpdKeys.RapToKlicensee(storedRap), store.Klicensee);
        Assert.Equal("rap-store", store.Source);
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
