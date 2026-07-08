using PkgLens.Core.Formats;
using PkgLens.Core.Formats.Ps3;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;

namespace PkgLens.Core;

/// <summary>
/// Convenience entry point. Selects a format reader (PS3 today) and parses a package from a
/// path or stream. This is the surface the CLI and GUI consume.
/// </summary>
public static class PkgReader
{
    /// <summary>Opens and parses a package file, resolving keys via <paramref name="keys"/> (file-based if null).</summary>
    public static PkgInfo Open(string path, IKeyProvider? keys = null)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, keys);
    }

    /// <summary>Parses a package from an already-open seekable stream.</summary>
    public static PkgInfo Read(Stream stream, IKeyProvider? keys = null)
    {
        keys ??= new FileKeyProvider();
        IPackageReader reader = SelectReader(stream);
        return reader.Read(stream, keys);
    }

    /// <summary>
    /// Decrypts a single entry's data into memory, resolving keys via <paramref name="keys"/>.
    /// Intended for small entries (icons, PARAM.SFO). Throws <see cref="PkgKeyException"/> if a
    /// required key is unavailable.
    /// </summary>
    public static byte[] ExtractEntryBytes(Stream stream, PkgHeader header, PkgEntry entry, IKeyProvider keys)
    {
        var decryptor = ResolveDecryptor(header, keys);
        try
        {
            return Ps3PackageReader.ReadEntry(stream, header, decryptor, entry);
        }
        finally
        {
            (decryptor as IDisposable)?.Dispose();
        }
    }

    /// <summary>Streams a single entry's decrypted data to <paramref name="destination"/>.</summary>
    public static void ExtractEntry(Stream stream, PkgHeader header, PkgEntry entry, Stream destination, IKeyProvider keys)
    {
        var decryptor = ResolveDecryptor(header, keys);
        try
        {
            Ps3PackageReader.CopyEntryTo(stream, header, decryptor, entry, destination);
        }
        finally
        {
            (decryptor as IDisposable)?.Dispose();
        }
    }

    private static Crypto.IPkgDecryptor ResolveDecryptor(PkgHeader header, IKeyProvider keys)
    {
        if (!keys.TryResolve(header, out var ctx, out string? reason))
            throw new PkgKeyException(reason ?? "No decryptor could be resolved for this package.");
        return ctx.CreateDecryptor();
    }

    private static IPackageReader SelectReader(Stream stream)
    {
        var ps3 = new Ps3PackageReader();
        if (ps3.CanRead(stream))
            return ps3;

        throw new PkgFormatException("Unrecognized package: no known format reader accepts it.");
    }
}
