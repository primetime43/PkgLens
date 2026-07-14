using System.Security.Cryptography;

namespace PkgLens.Core.Keys;

/// <summary>
/// Helpers for locating, installing, and identifying an <em>override</em> PS3 gpkg AES key. The
/// standard retail key is bundled (<see cref="BundledKeys.Ps3GpkgAesKey"/>) and used by default; these
/// helpers exist for the optional case where a user supplies their own key file (e.g. an IDU/kiosk
/// key). The SHA-256 <em>fingerprint</em> below lets the tool tell the user whether the key they
/// supplied is the standard one.
/// </summary>
public static class KeyStore
{
    /// <summary>The canonical filename PkgLens writes/reads within a keys directory.</summary>
    public const string PrimaryFileName = "ps3_gpkg_aes.key";

    /// <summary>
    /// SHA-256 of the 16 raw bytes of the standard PS3 gpkg (retail package) AES key. This is a
    /// fingerprint for recognition only; it cannot be reversed to recover the key.
    /// </summary>
    public const string KnownGpkgKeySha256 = "9f59799419060804ae46982ba023f56c897206b951daeb507b8dcb384e597b5f";

    /// <summary>The platform default keys directory: <c>%USERPROFILE%\.pkglens</c> / <c>~/.pkglens</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens");

    /// <summary>Parses a key from raw text: 32 hex chars (optional <c>0x</c> / whitespace) or a 16-byte token.</summary>
    public static byte[] ParseKeyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FileKeyProvider.ParseKeyFile(System.Text.Encoding.ASCII.GetBytes(text.Trim()));
    }

    /// <summary>
    /// Reads a key from a string that is either the key itself (hex/raw) or a path to a key file.
    /// </summary>
    public static byte[] ResolveKeyArgument(string hexOrPath)
    {
        if (File.Exists(hexOrPath))
            return FileKeyProvider.ParseKeyFile(File.ReadAllBytes(hexOrPath));
        return ParseKeyText(hexOrPath);
    }

    /// <summary>Writes <paramref name="key"/> (as hex) to the keys directory and returns the file path.</summary>
    public static string Install(ReadOnlySpan<byte> key, string? directory = null)
    {
        if (key.Length != 16)
            throw new ArgumentException("A PS3 gpkg AES key must be exactly 16 bytes.", nameof(key));

        string dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory!;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, PrimaryFileName);
        File.WriteAllText(path, Convert.ToHexString(key).ToLowerInvariant());
        return path;
    }

    /// <summary>True when <paramref name="key"/> matches the fingerprint of the standard gpkg key.</summary>
    public static bool IsKnownGpkgKey(ReadOnlySpan<byte> key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(key, hash);
        return Convert.ToHexString(hash).Equals(KnownGpkgKeySha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A friendly one-line description of a key, for import/status output.</summary>
    public static string Describe(ReadOnlySpan<byte> key) => IsKnownGpkgKey(key)
        ? "recognized as the standard NPDRM PKG PS3 AES key"
        : "unrecognized key — it will be used as-is (double-check it if retail packages fail to decrypt)";
}
