using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared.Keys;

/// <summary>
/// A key provider backed by an in-process key. Useful for tests and for callers that already
/// hold the retail AES key (e.g. read from a CLI option). Debug packages resolve regardless of
/// whether a key was supplied.
/// </summary>
public sealed class InMemoryKeyProvider : IKeyProvider
{
    private readonly byte[]? _aesKey;

    /// <param name="retailAesKey">The 16-byte PS3 gpkg AES key, or null if only debug packages are expected.</param>
    public InMemoryKeyProvider(byte[]? retailAesKey = null)
    {
        if (retailAesKey is { Length: not 16 })
            throw new ArgumentException("Retail AES key must be 16 bytes.", nameof(retailAesKey));
        _aesKey = retailAesKey;
    }

    public bool TryResolve(PkgHeader header, out DecryptionContext context, out string? reason)
    {
        reason = null;

        if (header.Finalization == PkgFinalization.Debug)
        {
            context = DecryptionContext.ForDebug(header);
            return true;
        }

        if (header.Finalization == PkgFinalization.Retail)
        {
            if (_aesKey is null)
            {
                context = null!;
                reason = "Retail package requires the NPDRM PKG PS3 AES key, but none was supplied.";
                return false;
            }
            context = DecryptionContext.ForRetail(header, _aesKey);
            return true;
        }

        context = null!;
        reason = $"Unknown finalization (raw 0x{header.RawFinalization:X4}); cannot select a decryptor.";
        return false;
    }
}
