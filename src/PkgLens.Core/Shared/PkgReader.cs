using PkgLens.Core.Shared.Formats;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

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
        return PkgContainerReader.ReadEntry(stream, header, decryptors.For(entry), entry);
    }

    /// <summary>Decrypts a bounded prefix of an entry for header-only inspection.</summary>
    public static byte[] ExtractEntryPrefix(Stream stream, PkgHeader header, PkgEntry entry,
        IKeyProvider keys, int maxBytes)
    {
        using var decryptors = ResolveDecryptorSet(header, keys);
        return PkgContainerReader.ReadEntryPrefix(stream, header, decryptors.For(entry), entry, maxBytes);
    }

    /// <summary>Decrypts a bounded range within an entry for random-access format inspection.</summary>
    public static byte[] ExtractEntryRange(Stream stream, PkgHeader header, PkgEntry entry,
        IKeyProvider keys, long entryOffset, int length)
    {
        using var decryptors = ResolveDecryptorSet(header, keys);
        return PkgContainerReader.ReadEntryRange(stream, header, decryptors.For(entry), entry, entryOffset, length);
    }

    /// <summary>Streams a single entry's decrypted data to <paramref name="destination"/>.</summary>
    public static void ExtractEntry(Stream stream, PkgHeader header, PkgEntry entry, Stream destination,
        IKeyProvider keys, CancellationToken cancellationToken = default, IProgress<long>? progress = null)
    {
        using var decryptors = ResolveDecryptorSet(header, keys);
        PkgContainerReader.CopyEntryTo(stream, header, decryptors.For(entry), entry, destination,
            cancellationToken: cancellationToken, progress: progress);
    }

    public static Stream OpenEntry(Stream stream, PkgHeader header, PkgEntry entry, IKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The package stream must be readable and seekable.", nameof(stream));
        if (entry.IsDirectory || entry.FileSize > long.MaxValue)
            throw new PkgFormatException($"Entry '{entry.Name}' cannot be opened as a file stream.");

        PkgDecryptorSet decryptors = ResolveDecryptorSet(header, keys);
        return new EntryReadStream(stream, header, entry, decryptors, decryptors.For(entry));
    }

    /// <summary>
    /// Extracts every file to <paramref name="outputDir"/>, rebuilding the package's directory tree.
    /// An optional <paramref name="filter"/> selects which entries to write; <paramref name="onExtracted"/>
    /// is called per file (for progress). Returns the number of files written. Entry names that would
    /// escape the output directory are rejected.
    /// </summary>
    public static int ExtractAll(Stream stream, PkgInfo info, string outputDir, IKeyProvider keys,
        Func<PkgEntry, bool>? filter = null, Action<PkgEntry>? onExtracted = null,
        CancellationToken cancellationToken = default, IProgress<PkgOperationProgress>? progress = null)
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
        long totalBytes = info.Entries.Where(entry => entry.IsFile && (filter is null || filter(entry)))
            .Sum(entry => checked((long)entry.FileSize));
        long completedBytes = 0;
        int count = 0;
        foreach (var entry in info.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter is not null && !filter(entry)) continue;

            string dest = SafeCombine(rootWithSep, entry.Name);
            EnsureNoLinkedDescendant(root, dest);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(dest);
                EnsureNoLinkedDescendant(root, dest);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            EnsureNoLinkedDescendant(root, Path.GetDirectoryName(dest)!);
            long entryBase = completedBytes;
            var entryProgress = progress is null ? null : new InlineProgress<long>(bytes =>
                progress.Report(new PkgOperationProgress(entryBase + bytes, totalBytes, entry.Name)));
            WriteAtomically(dest, fs =>
                PkgContainerReader.CopyEntryTo(stream, info.Header, decryptors.For(entry), entry, fs,
                    cancellationToken: cancellationToken, progress: entryProgress));
            completedBytes += checked((long)entry.FileSize);
            onExtracted?.Invoke(entry);
            count++;
        }
        return count;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class EntryReadStream(
        Stream source, PkgHeader header, PkgEntry entry, PkgDecryptorSet decryptors,
        IPkgDecryptor decryptor) : Stream
    {
        private readonly long _length = checked((long)entry.FileSize);
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, _length - _position);
            if (count <= 0) return 0;
            long absolute = checked((long)header.DataOffset + (long)entry.FileOffset + _position);
            source.Position = absolute;
            int total = 0;
            while (total < count)
            {
                int read = source.Read(buffer.Slice(total, count - total));
                if (read <= 0) throw new PkgFormatException($"Truncated PKG entry '{entry.Name}'.");
                total += read;
            }
            decryptor.DecryptInPlace(buffer[..count], checked((long)entry.FileOffset + _position));
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return target;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) decryptors.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private static void EnsureNoLinkedDescendant(string root, string destination)
    {
        string relative = Path.GetRelativePath(root, destination);
        string current = root;
        foreach (string part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new PkgFormatException(
                    $"Extraction path contains a symbolic link or junction: {current}");
        }
    }

    private static void WriteAtomically(string destination, Action<Stream> write)
    {
        string fullPath = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(fullPath)!;
        string temp = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup after a failed write */ }
        }
    }

    private static PkgDecryptorSet ResolveDecryptorSet(PkgHeader header, IKeyProvider keys)
    {
        if (!keys.TryResolve(header, out var ctx, out string? reason))
            throw new PkgKeyException(reason ?? "No decryptor could be resolved for this package.");
        return ctx.CreateDecryptorSet();
    }

    private static IPackageReader SelectReader(Stream stream)
    {
        var ps3 = new PkgContainerReader();
        if (ps3.CanRead(stream))
            return ps3;

        throw new PkgFormatException("Unrecognized package: no known format reader accepts it.");
    }
}
