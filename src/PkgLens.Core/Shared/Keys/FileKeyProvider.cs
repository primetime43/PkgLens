using System.Security.Cryptography;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared.Keys;

/// <summary>
/// Resolves the retail PS3 gpkg AES key. The standard key is public and <b>bundled</b>
/// (<see cref="BundledKeys.Ps3GpkgAesKey"/>), so retail packages decrypt with no setup. A
/// user-supplied key file, if present, joins the automatic candidate key ring for non-standard
/// packages. Standard and IDU/kiosk packages are detected automatically.
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
    private byte[]? _cachedOverrideKey;
    private string? _loadError;
    private bool _resolved;
    private readonly Dictionary<PkgHeader, DecryptionContext> _selectedContexts =
        new(ReferenceEqualityComparer.Instance);

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

        if (_selectedContexts.TryGetValue(header, out context!))
            return true;

        if (!TryGetPs3Candidates(header, out var candidates, out string? loadError, out _))
        {
            context = null!;
            reason = loadError;
            return false;
        }

        context = candidates[0];
        return true;
    }

    /// <summary>
    /// Returns every viable retail PS3 context. A valid header CMAC narrows the result to its exact
    /// key; unsigned or stale-header packages fall back to structural item-table validation by the
    /// container reader.
    /// </summary>
    internal bool TryGetPs3Candidates(PkgHeader header, out IReadOnlyList<DecryptionContext> contexts,
        out string? reason, out bool authenticated)
    {
        authenticated = false;
        reason = null;

        byte[]? overrideKey = LoadOverrideKey(out string? loadError);
        if (loadError is not null)
        {
            contexts = Array.Empty<DecryptionContext>();
            reason = loadError;
            return false;
        }

        var keys = new List<byte[]>(3);
        AddDistinct(keys, overrideKey);
        AddDistinct(keys, BundledKeys.Ps3GpkgAesKey);
        AddDistinct(keys, BundledKeys.Ps3IduAesKey);

        if (!IsAllZero(header.HeaderCmac))
        {
            var matches = keys.Where(key => HeaderCmacMatches(header, key)).ToList();
            if (matches.Count > 0)
            {
                keys = matches;
                authenticated = true;
            }
        }

        contexts = keys.Select(key => DecryptionContext.ForRetail(header, key)).ToArray();
        return true;
    }

    internal void RememberSelection(PkgHeader header, DecryptionContext context) =>
        _selectedContexts[header] = context;

    /// <summary>
    /// Returns a non-secret friendly name for the PS3 package key selected while reading
    /// <paramref name="header"/>, or null when no key has been selected for that header.
    /// </summary>
    public string? GetSelectedKeyName(PkgHeader header)
    {
        if (!_selectedContexts.TryGetValue(header, out DecryptionContext? context) ||
            !context.TryGetHeaderCmacKey(out byte[] selected))
            return null;
        if (selected.AsSpan().SequenceEqual(BundledKeys.Ps3GpkgAesKey))
            return "Bundled standard PS3 retail package AES key";
        if (selected.AsSpan().SequenceEqual(BundledKeys.Ps3IduAesKey))
            return "Bundled IDU/kiosk PS3 package AES key";

        byte[]? overrideKey = LoadOverrideKey(out _);
        return overrideKey is not null && selected.AsSpan().SequenceEqual(overrideKey)
            ? "User override PS3 package AES key"
            : "Custom PS3 retail package AES key";
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
    /// Loads the optional user key. Absence is not an error because bundled standard and IDU keys
    /// remain available as candidates.
    /// </summary>
    private byte[]? LoadOverrideKey(out string? error)
    {
        if (_resolved)
        {
            error = _loadError;
            return _cachedOverrideKey;
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
                    _cachedOverrideKey = ParseKeyFile(File.ReadAllBytes(path));
                    return _cachedOverrideKey;
                }
                catch (Exception ex)
                {
                    _loadError = $"Found key file '{path}' but could not parse it: {ex.Message}";
                    error = _loadError;
                    return null;
                }
            }
        }

        return null;
    }

    private static void AddDistinct(List<byte[]> keys, byte[]? candidate)
    {
        if (candidate is not null && !keys.Any(key => key.AsSpan().SequenceEqual(candidate)))
            keys.Add(candidate);
    }

    private static bool HeaderCmacMatches(PkgHeader header, byte[] key)
    {
        byte[] computed = AesCmac.Compute(key, header.AuthenticatedHeader);
        return CryptographicOperations.FixedTimeEquals(computed, header.HeaderCmac);
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        foreach (byte item in value)
            if (item != 0) return false;
        return true;
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
