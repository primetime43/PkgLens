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
        using var decryptors = ResolveDecryptorSet(header, keys);
        return Ps3PackageReader.ReadEntry(stream, header, decryptors.For(entry), entry);
    }

    /// <summary>Streams a single entry's decrypted data to <paramref name="destination"/>.</summary>
    public static void ExtractEntry(Stream stream, PkgHeader header, PkgEntry entry, Stream destination, IKeyProvider keys)
    {
        using var decryptors = ResolveDecryptorSet(header, keys);
        Ps3PackageReader.CopyEntryTo(stream, header, decryptors.For(entry), entry, destination);
    }

    /// <summary>
    /// Extracts every file to <paramref name="outputDir"/>, rebuilding the package's directory tree.
    /// An optional <paramref name="filter"/> selects which entries to write; <paramref name="onExtracted"/>
    /// is called per file (for progress). Returns the number of files written. Entry names that would
    /// escape the output directory are rejected.
    /// </summary>
    public static int ExtractAll(Stream stream, PkgInfo info, string outputDir, IKeyProvider keys,
        Func<PkgEntry, bool>? filter = null, Action<PkgEntry>? onExtracted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);
        if (!info.IsDecrypted)
            throw new PkgFormatException("Package contents are not decrypted — a key is required to extract.");

        string root = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(root);
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        using var decryptors = ResolveDecryptorSet(info.Header, keys);
        int count = 0;
        foreach (var entry in info.Entries)
        {
            if (filter is not null && !filter(entry)) continue;

            string dest = SafeCombine(rootWithSep, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var fs = File.Create(dest))
                Ps3PackageReader.CopyEntryTo(stream, info.Header, decryptors.For(entry), entry, fs);
            onExtracted?.Invoke(entry);
            count++;
        }
        return count;
    }

    private static string SafeCombine(string rootWithSep, string relative)
    {
        string rel = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(rootWithSep, rel));
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal) &&
            full != rootWithSep.TrimEnd(Path.DirectorySeparatorChar))
            throw new PkgFormatException($"Entry '{relative}' would escape the output directory.");
        return full;
    }

    private static PkgDecryptorSet ResolveDecryptorSet(PkgHeader header, IKeyProvider keys)
    {
        if (!keys.TryResolve(header, out var ctx, out string? reason))
            throw new PkgKeyException(reason ?? "No decryptor could be resolved for this package.");
        return ctx.CreateDecryptorSet();
    }

    private static IPackageReader SelectReader(Stream stream)
    {
        var ps3 = new Ps3PackageReader();
        if (ps3.CanRead(stream))
            return ps3;

        throw new PkgFormatException("Unrecognized package: no known format reader accepts it.");
    }
}
