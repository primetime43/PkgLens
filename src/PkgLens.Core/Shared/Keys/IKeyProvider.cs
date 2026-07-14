using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared.Keys;

/// <summary>
/// Resolves the decryption context for a package. The standard retail key is public and bundled
/// (<see cref="BundledKeys"/>), so retail packages resolve by default; a user-supplied key file can
/// override it. Debug packages always resolve because their keystream is self-contained.
/// </summary>
public interface IKeyProvider
{
    /// <summary>
    /// Attempts to resolve a <see cref="DecryptionContext"/> for <paramref name="header"/>.
    /// Returns false (with a reason) when a required key is unavailable — e.g. a retail package
    /// and no key file on disk. Header/metadata/content-id parsing does not require this.
    /// </summary>
    bool TryResolve(PkgHeader header, out DecryptionContext context, out string? reason);
}
