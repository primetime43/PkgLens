using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared.Keys;

/// <summary>
/// The decryptor(s) for a package. Most packages (PS3, PSVita) use a single key for the whole data
/// region. PSP/PSX packages are the exception: the item table uses the PSP key, but each entry's
/// <em>name and data</em> use a per-entry key chosen by the record's type high-byte — <c>0x90</c>
/// selects the PSP key, anything else the PS3 gpkg key (matching pkg2zip's
/// <c>psp_type == 0x90 ? key : ps3_key</c>). This type owns disposal of the decryptors it holds.
/// </summary>
public sealed class PkgDecryptorSet : IDisposable
{
    /// <summary>Type high-byte value that selects the primary (PSP) key for an entry.</summary>
    private const uint PspKeyMarker = 0x90;

    private readonly IPkgDecryptor _primary;
    private readonly IPkgDecryptor? _secondary;

    public PkgDecryptorSet(IPkgDecryptor primary, IPkgDecryptor? secondary = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary;
    }

    /// <summary>Decryptor for the item table (and, for uniform packages, everything).</summary>
    public IPkgDecryptor Table => _primary;

    /// <summary>True when entries use per-entry key selection (PSP/PSX only).</summary>
    public bool PerEntry => _secondary is not null;

    /// <summary>
    /// The decryptor for a specific entry's name/data. Uniform packages always return the primary;
    /// PSP/PSX return the primary for type-<c>0x90</c> entries and the gpkg secondary otherwise.
    /// </summary>
    public IPkgDecryptor For(PkgEntry entry)
    {
        if (_secondary is null) return _primary;
        uint typeHigh = (entry.RawType >> 24) & 0xFF;
        return typeHigh == PspKeyMarker ? _primary : _secondary;
    }

    public void Dispose()
    {
        (_primary as IDisposable)?.Dispose();
        (_secondary as IDisposable)?.Dispose();
    }
}
