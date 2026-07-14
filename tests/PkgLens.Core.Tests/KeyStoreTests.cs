using PkgLens.Core.Shared.Keys;
using Xunit;

namespace PkgLens.Core.Tests;

public class KeyStoreTests
{
    // The standard PS3 gpkg retail package key (public; bundled as BundledKeys.Ps3GpkgAesKey).
    // Duplicated here to verify the fingerprint logic recognizes it.
    private static readonly byte[] GpkgKey =
    {
        0x2E, 0x7B, 0x71, 0xD7, 0xC9, 0xC9, 0xA1, 0x4E,
        0xA3, 0x22, 0x1F, 0x18, 0x88, 0x28, 0xB8, 0xF8,
    };

    [Fact]
    public void IsKnownGpkgKey_RecognizesStandardKey()
    {
        Assert.True(KeyStore.IsKnownGpkgKey(GpkgKey));
        Assert.Contains("recognized", KeyStore.Describe(GpkgKey));
    }

    [Fact]
    public void IsKnownGpkgKey_RejectsOtherKeys()
    {
        Assert.False(KeyStore.IsKnownGpkgKey(new byte[16]));
        Assert.Contains("unrecognized", KeyStore.Describe(new byte[16]));
    }

    [Fact]
    public void ParseKeyText_AcceptsHexWithPrefixAndWhitespace()
    {
        var parsed = KeyStore.ParseKeyText("  0x2E7B71D7C9C9A14EA3221F188828B8F8  ");
        Assert.Equal(GpkgKey, parsed);
    }

    [Fact]
    public void Install_WritesReadableKeyFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pkglens-keystore-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = KeyStore.Install(GpkgKey, dir);
            Assert.True(File.Exists(path));
            Assert.EndsWith(KeyStore.PrimaryFileName, path);

            // Round-trip: a FileKeyProvider pointed here must resolve a retail package.
            var provider = new FileKeyProvider(dir);
            Assert.True(provider.TryLocateKeyFile(out string found));
            Assert.Equal(GpkgKey, KeyStore.ResolveKeyArgument(found));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Install_RejectsWrongLengthKey()
    {
        Assert.Throws<ArgumentException>(() => KeyStore.Install(new byte[8]));
    }
}
