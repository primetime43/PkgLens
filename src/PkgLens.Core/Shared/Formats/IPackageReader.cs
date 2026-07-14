using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared.Formats;

/// <summary>
/// Common surface for all package formats (PS3 first; PSP/PSVita/PS4 slot in later). A reader
/// takes a seekable stream plus a key provider and returns models. It never opens files, shows
/// UI, or hardcodes keys.
/// </summary>
public interface IPackageReader
{
    /// <summary>True if this reader recognizes the package at the start of <paramref name="stream"/>.</summary>
    bool CanRead(Stream stream);

    /// <summary>
    /// Parses the package. Always fills header/metadata; fills entries/SFO when a decryptor is
    /// available. Throws <see cref="PkgFormatException"/> on malformed input.
    /// </summary>
    PkgInfo Read(Stream stream, IKeyProvider keys);
}
