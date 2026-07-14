using PkgLens.Core.Models;

namespace PkgLens.Core.Keys;

/// <summary>
/// Resolves the retail PS3 gpkg AES key. The standard key is public and <b>bundled</b>
/// (<see cref="BundledKeys.Ps3GpkgAesKey"/>), so retail packages decrypt with no setup. A
/// user-supplied key file, if present, <b>overrides</b> the bundled key — useful for the IDU/kiosk
/// key or any non-standard package.
///
/// Override-file resolution order for the keys directory:
/// <list type="number">
///   <item>an explicit directory passed to the constructor (e.g. the CLI <c>--keys</c> option),</item>
///   <item>the <c>PKGLENS_KEYS</c> environment variable,</item>
///   <item>the platform default: <c>%USERPROFILE%\.pkglens</c> / <c>~/.pkglens</c>.</item>
/// </list>
/// Within that directory the key is read from the first present of
/// <c>ps3_gpkg_aes.key</c>, <c>ps3_gpkg.key</c>, or <c>gpkg_key</c>. A key file may contain the
/// 16 raw bytes, or 32 hex characters (whitespace / <c>0x</c> prefixes tolerated). If no override
/// file exists, the bundled key is used.
/// </summary>
public sealed class FileKeyProvider : IKeyProvider
{
    public const string EnvVar = "PKGLENS_KEYS";

    private static readonly string[] CandidateFileNames =
    {
        "ps3_gpkg_aes.key", "ps3_gpkg.key", "gpkg_key",
    };

    private readonly string? _explicitDir;
    private byte[]? _cachedKey;
    private string? _loadError;
    private bool _resolved;

    public FileKeyProvider(string? explicitKeysDirectory = null) => _explicitDir = explicitKeysDirectory;

    public bool TryResolve(PkgHeader header, out DecryptionContext context, out string? reason)
    {
        reason = null;

        if (header.Finalization == PkgFinalization.Debug)
        {
            context = DecryptionContext.ForDebug(header);
            return true;
        }

        if (header.Finalization != PkgFinalization.Retail)
        {
            context = null!;
            reason = $"Unknown finalization (raw 0x{header.RawFinalization:X4}); cannot select a decryptor.";
            return false;
        }

        // PSP / PSVita packages use their own bundled keys, selected by the header key_type.
        if (header.IsPspPsVita)
            return TryResolvePspVita(header, out context, out reason);

        if (!header.IsPs3)
        {
            context = null!;
            reason = $"Unsupported package platform (raw 0x{header.RawPlatform:X4}).";
            return false;
        }

        byte[]? key = LoadKey(out string? loadError);
        if (key is null)
        {
            // A key is only ever null here when a user placed an override file we could not parse;
            // a missing override falls back to the bundled key. Surface the parse error loudly.
            context = null!;
            reason = loadError;
            return false;
        }

        context = DecryptionContext.ForRetail(header, key);
        return true;
    }

    /// <summary>
    /// Resolves a PSP/PSVita decryptor from the bundled keys. key_type 1 = PSP (key used directly);
    /// 2/3/4 = PSVita (the CTR key is derived from the data_riv). These keys are public and always
    /// present, so PSP/PSVita packages decrypt out of the box like retail PS3 packages.
    /// </summary>
    private static bool TryResolvePspVita(PkgHeader header, out DecryptionContext context, out string? reason)
    {
        reason = null;
        switch (header.PspKeyType)
        {
            case 1:
                context = DecryptionContext.ForPsp(header, BundledKeys.PspPkgAesKey);
                return true;
            case 2:
                context = DecryptionContext.ForVita(header, BundledKeys.VitaPkgAesKey2);
                return true;
            case 3:
                context = DecryptionContext.ForVita(header, BundledKeys.VitaPkgAesKey3);
                return true;
            case 4:
                context = DecryptionContext.ForVita(header, BundledKeys.VitaPkgAesKey4);
                return true;
            default:
                context = null!;
                reason = $"Unsupported PSP/PSVita key type {header.PspKeyType} (header[0xE7] & 7).";
                return false;
        }
    }

    /// <summary>
    /// Returns the retail key to use: a user override file if one is present, otherwise the bundled
    /// public key. Only returns null when an override file exists but cannot be parsed (a real error
    /// the user should see); a simple absence of any file is not an error.
    /// </summary>
    private byte[]? LoadKey(out string? error)
    {
        if (_resolved)
        {
            error = _cachedKey is null ? _loadError : null;
            return _cachedKey;
        }

        _resolved = true;
        error = null;

        foreach (string dir in CandidateDirectories())
        {
            foreach (string name in CandidateFileNames)
            {
                string path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    _cachedKey = ParseKeyFile(File.ReadAllBytes(path));
                    return _cachedKey;
                }
                catch (Exception ex)
                {
                    _loadError = $"Found key file '{path}' but could not parse it: {ex.Message}";
                    error = _loadError;
                    return null;
                }
            }
        }

        // No override file: use the bundled public key so retail packages decrypt out of the box.
        _cachedKey = BundledKeys.Ps3GpkgAesKey;
        return _cachedKey;
    }

    /// <summary>The key filenames this provider recognizes, most-preferred first.</summary>
    public static IReadOnlyList<string> KeyFileNames => CandidateFileNames;

    /// <summary>The keys directories searched, in order (explicit → env var → platform default).</summary>
    public IEnumerable<string> SearchDirectories() => CandidateDirectories();

    /// <summary>Finds the first existing key file across the search directories, if any.</summary>
    public bool TryLocateKeyFile(out string path)
    {
        foreach (string dir in CandidateDirectories())
            foreach (string name in CandidateFileNames)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) { path = candidate; return true; }
            }
        path = string.Empty;
        return false;
    }

    private IEnumerable<string> CandidateDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_explicitDir))
            yield return _explicitDir!;

        string? env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            yield return env!;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".pkglens");
    }

    /// <summary>Accepts a 16-byte raw key or a hex string (with optional whitespace / 0x prefix).</summary>
    internal static byte[] ParseKeyFile(byte[] contents)
    {
        if (contents.Length == 16)
            return contents;

        string text = System.Text.Encoding.ASCII.GetString(contents).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        text = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

        if (text.Length != 32)
            throw new FormatException(
                $"expected 16 raw bytes or 32 hex characters, got {contents.Length} bytes / {text.Length} hex chars.");

        return Convert.FromHexString(text);
    }
}
