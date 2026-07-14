using PkgLens.Core.Crypto;
using PkgLens.Core.Models;

namespace PkgLens.Core.Keys;

/// <summary>
/// The resolved means of decrypting a specific package: which scheme applies and any key
/// material required to build a decryptor for it. For debug packages this needs nothing beyond
/// the header; for retail packages it carries the runtime-supplied AES key.
/// </summary>
public sealed class DecryptionContext
{
    public PkgFinalization Scheme { get; }

    private readonly byte[]? _aesKey;
    private readonly byte[] _dataRiv;
    private readonly byte[] _qaDigest;

    private DecryptionContext(PkgFinalization scheme, byte[]? aesKey, byte[] dataRiv, byte[] qaDigest)
    {
        Scheme = scheme;
        _aesKey = aesKey;
        _dataRiv = dataRiv;
        _qaDigest = qaDigest;
    }

    /// <summary>Debug packages need no external key — the keystream comes from the QA digest.</summary>
    public static DecryptionContext ForDebug(PkgHeader header) =>
        new(PkgFinalization.Debug, null, header.DataRiv, header.QaDigest);

    /// <summary>Retail packages need the 16-byte PS3 gpkg AES key supplied at runtime.</summary>
    public static DecryptionContext ForRetail(PkgHeader header, ReadOnlySpan<byte> aesKey) =>
        new(PkgFinalization.Retail, aesKey.ToArray(), header.DataRiv, header.QaDigest);

    /// <summary>Builds a fresh decryptor. The caller owns disposal if the result is <see cref="IDisposable"/>.</summary>
    public IPkgDecryptor CreateDecryptor() => Scheme switch
    {
        PkgFinalization.Debug => new DebugSha1Decryptor(_qaDigest),
        PkgFinalization.Retail => new RetailAesCtrDecryptor(
            _aesKey ?? throw new PkgKeyException("Retail package requires an AES key."),
            _dataRiv),
        _ => throw new PkgKeyException($"Unsupported decryption scheme: {Scheme}."),
    };

    /// <summary>
    /// Exposes the AES key used to verify the header CMAC (retail only). Returns false for debug
    /// packages, which are non-finalized and carry no CMAC.
    /// </summary>
    public bool TryGetHeaderCmacKey(out byte[] key)
    {
        if (Scheme == PkgFinalization.Retail && _aesKey is not null)
        {
            key = _aesKey;
            return true;
        }
        key = Array.Empty<byte>();
        return false;
    }
}
