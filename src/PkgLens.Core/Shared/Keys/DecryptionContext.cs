using System.Security.Cryptography;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared.Keys;

/// <summary>
/// The resolved means of decrypting a specific package: which scheme applies and any key
/// material required to build a decryptor for it. For debug packages this needs nothing beyond
/// the header; for retail packages it carries the runtime-supplied AES key. PS3, PSP and PSVita
/// finalized packages all share the same AES-128-CTR data cipher, differing only in the key.
/// </summary>
public sealed class DecryptionContext
{
    public PkgFinalization Scheme { get; }

    private readonly byte[]? _aesKey;
    private readonly byte[]? _secondaryKey;
    private readonly byte[] _dataRiv;
    private readonly byte[] _qaDigest;
    private readonly bool _gpkgCmac;

    private DecryptionContext(PkgFinalization scheme, byte[]? aesKey, byte[] dataRiv, byte[] qaDigest,
        bool gpkgCmac, byte[]? secondaryKey = null)
    {
        Scheme = scheme;
        _aesKey = aesKey;
        _secondaryKey = secondaryKey;
        _dataRiv = dataRiv;
        _qaDigest = qaDigest;
        _gpkgCmac = gpkgCmac;
    }

    /// <summary>Debug packages need no external key — the keystream comes from the QA digest.</summary>
    public static DecryptionContext ForDebug(PkgHeader header) =>
        new(PkgFinalization.Debug, null, header.DataRiv, header.QaDigest, gpkgCmac: false);

    /// <summary>Retail PS3 packages: AES-128-CTR with the 16-byte gpkg key, which also keys the header CMAC.</summary>
    public static DecryptionContext ForRetail(PkgHeader header, ReadOnlySpan<byte> aesKey) =>
        new(PkgFinalization.Retail, aesKey.ToArray(), header.DataRiv, header.QaDigest, gpkgCmac: true);

    /// <summary>
    /// PSP / PSX packages (key_type 1): the item table uses the PSP key (AES-128-CTR), but entries
    /// select per record type — <c>0x90</c> keeps the PSP key, others use the PS3 gpkg key. So both
    /// keys are carried here. The header CMAC is not the PS3 gpkg scheme, so it is not exposed.
    /// </summary>
    public static DecryptionContext ForPsp(PkgHeader header, ReadOnlySpan<byte> pspKey) =>
        new(PkgFinalization.Retail, pspKey.ToArray(), header.DataRiv, header.QaDigest, gpkgCmac: false,
            secondaryKey: BundledKeys.Ps3GpkgAesKey);

    /// <summary>
    /// PSVita packages (key_type 2/3/4): the per-package CTR key is derived by AES-ECB-encrypting the
    /// data_riv with the selected Vita key (pkg2zip <c>aes128_ecb_encrypt(vita_key, iv, main_key)</c>).
    /// </summary>
    public static DecryptionContext ForVita(PkgHeader header, ReadOnlySpan<byte> vitaKey)
    {
        byte[] mainKey = AesEcbEncryptBlock(vitaKey, header.DataRiv);
        return new(PkgFinalization.Retail, mainKey, header.DataRiv, header.QaDigest, gpkgCmac: false);
    }

    private static byte[] AesEcbEncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block)
    {
        if (key.Length != 16) throw new ArgumentException("Vita package key must be 16 bytes.", nameof(key));
        if (block.Length != 16) throw new ArgumentException("data_riv must be 16 bytes.", nameof(block));
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        return aes.EncryptEcb(block, PaddingMode.None);
    }

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
    /// Builds the decryptor set for the package: the primary decryptor plus, for PSP/PSX packages, a
    /// secondary gpkg decryptor for the per-entry key selection. The caller owns disposal.
    /// </summary>
    public PkgDecryptorSet CreateDecryptorSet()
    {
        var primary = CreateDecryptor();
        IPkgDecryptor? secondary = _secondaryKey is not null
            ? new RetailAesCtrDecryptor(_secondaryKey, _dataRiv)
            : null;
        return new PkgDecryptorSet(primary, secondary);
    }

    /// <summary>
    /// Exposes the AES key used to verify the header CMAC (retail only). Returns false for debug
    /// packages, which are non-finalized and carry no CMAC.
    /// </summary>
    public bool TryGetHeaderCmacKey(out byte[] key)
    {
        if (_gpkgCmac && Scheme == PkgFinalization.Retail && _aesKey is not null)
        {
            key = _aesKey;
            return true;
        }
        key = Array.Empty<byte>();
        return false;
    }
}
