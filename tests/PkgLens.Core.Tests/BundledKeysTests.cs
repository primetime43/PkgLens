using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class BundledKeysTests
{
    [Fact]
    public void BundledGpkgKey_MatchesKnownFingerprint()
    {
        // The bundled key must be exactly the standard retail key the fingerprint recognizes.
        Assert.True(KeyStore.IsKnownGpkgKey(BundledKeys.Ps3GpkgAesKey));
    }

    [Fact]
    public void FileKeyProvider_DecryptsRetailPackage_WithNoKeyFile()
    {
        // A retail package keyed with the standard (bundled) key must decrypt with no override file.
        var builder = new SyntheticPkgBuilder
        {
            Finalization = PkgFinalization.Retail,
            RetailAesKey = BundledKeys.Ps3GpkgAesKey,
        };
        builder.AddFile("USRDIR/DATA.BIN", new byte[] { 1, 2, 3, 4, 5 });
        byte[] pkg = builder.Build();

        using var emptyDir = new TempDir();
        var provider = new FileKeyProvider(emptyDir.Path);
        if (provider.TryLocateKeyFile(out _))
            return; // machine has an ambient override key; skip the bundled-fallback assertion.

        using var stream = new MemoryStream(pkg);
        var info = PkgReader.Read(stream, provider);

        Assert.True(info.IsDecrypted);
        var entry = info.Entries.Single(e => e.Name == "USRDIR/DATA.BIN");
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 },
            PkgReader.ExtractEntryBytes(stream, info.Header, entry, provider));
    }

    [Fact]
    public void FileKeyProvider_OverrideKeyFile_TakesPrecedenceOverBundled()
    {
        // A package keyed with a non-standard key must decrypt only via an installed override file.
        byte[] customKey = Enumerable.Range(0, 16).Select(i => (byte)(0x11 * i)).ToArray();
        var builder = new SyntheticPkgBuilder
        {
            Finalization = PkgFinalization.Retail,
            RetailAesKey = customKey,
        };
        builder.AddFile("USRDIR/DATA.BIN", new byte[] { 9, 8, 7 });
        byte[] pkg = builder.Build();

        using var dir = new TempDir();
        KeyStore.Install(customKey, dir.Path);

        var provider = new FileKeyProvider(dir.Path);
        using var stream = new MemoryStream(pkg);
        var info = PkgReader.Read(stream, provider);

        Assert.True(info.IsDecrypted);
        var entry = info.Entries.Single(e => e.Name == "USRDIR/DATA.BIN");
        Assert.Equal(new byte[] { 9, 8, 7 },
            PkgReader.ExtractEntryBytes(stream, info.Header, entry, provider));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pkglens-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
