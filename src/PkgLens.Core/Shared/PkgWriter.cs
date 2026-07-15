using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

/// <summary>
/// Rebuilds ("repacks") a package to a new stream, optionally replacing the data of individual
/// entries. The header CMAC and ECDSA signature are <b>not</b> recomputed — PkgLens never forges
/// signatures — so a repacked <em>retail</em> package is unsigned and will not pass integrity
/// checks or install on a stock console.
///
/// Layout of the rebuilt (encrypted) data region: <c>[item table | entry names | file data]</c>,
/// with fresh offsets/sizes. Everything before <c>data_offset</c> (header + metadata) is copied
/// from the source in chunks, with only <c>total_size</c> and <c>data_size</c> patched. Unchanged
/// entry data is decrypted from its old offset and re-encrypted at its new offset while streaming,
/// so package size is not limited by available memory.
/// </summary>
public static class PkgWriter
{
    private const int BufferSize = 1 << 20;
    private const int PatchedHeaderLength = 0x30;

    public static void Repack(
        Stream source,
        PkgInfo info,
        IReadOnlyDictionary<PkgEntry, byte[]> replacements,
        IKeyProvider keys,
        Stream destination,
        CancellationToken cancellationToken = default,
        IProgress<PkgOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanSeek)
            throw new PkgFormatException("A seekable source stream is required to repack a package.");
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        if (ReferenceEquals(source, destination))
            throw new ArgumentException("Source and destination must be different streams.", nameof(destination));

        var header = info.Header;
        if (!info.IsDecrypted)
            throw new PkgFormatException("Cannot repack a package whose contents were not decrypted.");
        if (!keys.TryResolve(header, out var context, out string? reason))
            throw new PkgKeyException(reason ?? "No key available to re-encrypt the package.");

        var entries = info.Entries;
        int itemCount = entries.Count;
        int tableLength;
        try { tableLength = checked(itemCount * PkgEntry.RecordSize); }
        catch (OverflowException) { throw new PkgFormatException("The package has too many entries to repack."); }

        var names = new byte[itemCount][];
        var nameOffsets = new long[itemCount];
        var fileOffsets = new long[itemCount];
        var fileSizes = new long[itemCount];

        long position = tableLength;
        try
        {
            for (int i = 0; i < itemCount; i++)
            {
                names[i] = Encoding.UTF8.GetBytes(entries[i].Name);
                if ((ulong)position > uint.MaxValue)
                    throw new PkgFormatException("The repacked entry-name table exceeds the PKG u32 offset limit.");
                nameOffsets[i] = position;
                position = checked(position + names[i].Length);
            }

            for (int i = 0; i < itemCount; i++)
            {
                PkgEntry entry = entries[i];
                long size = ReplacementOrOriginalSize(entry, replacements);
                fileSizes[i] = size;
                if (size == 0) continue;

                fileOffsets[i] = position;
                position = checked(position + size);
            }
        }
        catch (OverflowException)
        {
            throw new PkgFormatException("The repacked package is too large for this runtime.");
        }

        long dataSize = position;
        long dataOffset = ValidateDataOffset(header, source.Length);
        long totalSize;
        try { totalSize = checked(dataOffset + dataSize); }
        catch (OverflowException) { throw new PkgFormatException("The repacked total size exceeds the stream limit."); }

        byte[] buffer = new byte[BufferSize];
        CopyPatchedPrefix(source, destination, dataOffset, totalSize, dataSize, buffer);

        using var decryptors = context.CreateDecryptorSet();
        WriteItemTable(destination, entries, names, nameOffsets, fileOffsets, fileSizes,
            tableLength, decryptors.Table);
        WriteNames(destination, entries, names, nameOffsets, decryptors);
        WriteFiles(source, destination, header, entries, replacements, fileOffsets, fileSizes,
            decryptors, buffer, cancellationToken, progress);
    }

    private static long ReplacementOrOriginalSize(PkgEntry entry,
        IReadOnlyDictionary<PkgEntry, byte[]> replacements)
    {
        if (entry.IsDirectory) return 0;
        if (replacements.TryGetValue(entry, out byte[]? replacement))
            return replacement.LongLength;
        if (entry.FileSize > long.MaxValue)
            throw new PkgFormatException($"Entry '{entry.Name}' is too large for this runtime.");
        return (long)entry.FileSize;
    }

    private static long ValidateDataOffset(PkgHeader header, long sourceLength)
    {
        if (header.DataOffset < PatchedHeaderLength || header.DataOffset > (ulong)sourceLength ||
            header.DataOffset > long.MaxValue)
            throw new PkgFormatException($"Invalid data_offset 0x{header.DataOffset:X} for repack.");
        return (long)header.DataOffset;
    }

    private static void CopyPatchedPrefix(Stream source, Stream destination, long dataOffset,
        long totalSize, long dataSize, byte[] buffer)
    {
        var first = new byte[PatchedHeaderLength];
        source.Position = 0;
        ReadExact(source, first, first.Length, "header");
        BinaryPrimitives.WriteUInt64BigEndian(first.AsSpan(0x18), (ulong)totalSize);
        BinaryPrimitives.WriteUInt64BigEndian(first.AsSpan(0x28), (ulong)dataSize);
        destination.Write(first);

        CopyExact(source, destination, dataOffset - first.Length, buffer, "header/metadata prefix");
    }

    private static void WriteItemTable(Stream destination, IReadOnlyList<PkgEntry> entries,
        IReadOnlyList<byte[]> names, IReadOnlyList<long> nameOffsets, IReadOnlyList<long> fileOffsets,
        IReadOnlyList<long> fileSizes, int tableLength, IPkgDecryptor tableDecryptor)
    {
        var table = new byte[tableLength];
        for (int i = 0; i < entries.Count; i++)
        {
            var record = table.AsSpan(i * PkgEntry.RecordSize, PkgEntry.RecordSize);
            BinaryPrimitives.WriteUInt32BigEndian(record[0x00..], checked((uint)nameOffsets[i]));
            BinaryPrimitives.WriteUInt32BigEndian(record[0x04..], checked((uint)names[i].Length));
            BinaryPrimitives.WriteUInt64BigEndian(record[0x08..], (ulong)fileOffsets[i]);
            BinaryPrimitives.WriteUInt64BigEndian(record[0x10..], (ulong)fileSizes[i]);
            BinaryPrimitives.WriteUInt32BigEndian(record[0x18..], entries[i].RawType);
        }

        tableDecryptor.DecryptInPlace(table, 0);
        destination.Write(table);
    }

    private static void WriteNames(Stream destination, IReadOnlyList<PkgEntry> entries,
        IReadOnlyList<byte[]> names, IReadOnlyList<long> nameOffsets, PkgDecryptorSet decryptors)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            byte[] name = names[i];
            decryptors.For(entries[i]).DecryptInPlace(name, nameOffsets[i]);
            destination.Write(name);
        }
    }

    private static void WriteFiles(Stream source, Stream destination, PkgHeader header,
        IReadOnlyList<PkgEntry> entries, IReadOnlyDictionary<PkgEntry, byte[]> replacements,
        IReadOnlyList<long> fileOffsets, IReadOnlyList<long> fileSizes,
        PkgDecryptorSet decryptors, byte[] buffer, CancellationToken cancellationToken,
        IProgress<PkgOperationProgress>? progress)
    {
        long totalBytes = fileSizes.Sum();
        long completedBytes = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileSizes[i] == 0) continue;

            PkgEntry entry = entries[i];
            IPkgDecryptor decryptor = decryptors.For(entry);
            if (replacements.TryGetValue(entry, out byte[]? replacement) && !entry.IsDirectory)
                WriteReplacement(destination, replacement, fileOffsets[i], decryptor, buffer,
                    cancellationToken, progress, completedBytes, totalBytes, entry.Name);
            else
                CopyOriginalEntry(source, destination, header, entry, fileOffsets[i], decryptor, buffer,
                    cancellationToken, progress, completedBytes, totalBytes);
            completedBytes += fileSizes[i];
        }
    }

    private static void WriteReplacement(Stream destination, byte[] replacement, long newOffset,
        IPkgDecryptor decryptor, byte[] buffer, CancellationToken cancellationToken,
        IProgress<PkgOperationProgress>? progress, long completedBeforeEntry, long totalBytes, string name)
    {
        int sourceOffset = 0;
        while (sourceOffset < replacement.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(buffer.Length, replacement.Length - sourceOffset);
            replacement.AsSpan(sourceOffset, count).CopyTo(buffer);
            decryptor.DecryptInPlace(buffer.AsSpan(0, count), checked(newOffset + sourceOffset));
            destination.Write(buffer, 0, count);
            sourceOffset += count;
            progress?.Report(new PkgOperationProgress(completedBeforeEntry + sourceOffset, totalBytes, name));
        }
    }

    private static void CopyOriginalEntry(Stream source, Stream destination, PkgHeader header,
        PkgEntry entry, long newOffset, IPkgDecryptor decryptor, byte[] buffer,
        CancellationToken cancellationToken, IProgress<PkgOperationProgress>? progress,
        long completedBeforeEntry, long totalBytes)
    {
        ValidateOriginalRange(source, header, entry, out long absoluteOffset, out long size);
        source.Position = absoluteOffset;

        long copied = 0;
        while (copied < size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, size - copied);
            ReadExact(source, buffer, count, $"entry '{entry.Name}'");
            decryptor.DecryptInPlace(buffer.AsSpan(0, count), checked((long)entry.FileOffset + copied));
            decryptor.DecryptInPlace(buffer.AsSpan(0, count), checked(newOffset + copied));
            destination.Write(buffer, 0, count);
            copied += count;
            progress?.Report(new PkgOperationProgress(completedBeforeEntry + copied, totalBytes, entry.Name));
        }
    }

    private static void ValidateOriginalRange(Stream source, PkgHeader header, PkgEntry entry,
        out long absoluteOffset, out long size)
    {
        if (entry.FileOffset > header.DataSize || entry.FileSize > header.DataSize - entry.FileOffset)
            throw new PkgFormatException($"Entry '{entry.Name}' data range exceeds data_size.");
        if (header.DataOffset > (ulong)source.Length ||
            entry.FileOffset > (ulong)source.Length - header.DataOffset)
            throw new PkgFormatException($"Truncated PKG: entry '{entry.Name}' starts past end of file.");

        ulong absolute = header.DataOffset + entry.FileOffset;
        if (entry.FileSize > (ulong)source.Length - absolute || absolute > long.MaxValue ||
            entry.FileSize > long.MaxValue)
            throw new PkgFormatException($"Truncated PKG: entry '{entry.Name}' extends past end of file.");

        absoluteOffset = (long)absolute;
        size = (long)entry.FileSize;
    }

    private static void CopyExact(Stream source, Stream destination, long length, byte[] buffer, string what)
    {
        long copied = 0;
        while (copied < length)
        {
            int count = (int)Math.Min(buffer.Length, length - copied);
            ReadExact(source, buffer, count, what);
            destination.Write(buffer, 0, count);
            copied += count;
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer, int count, string what)
    {
        int read = 0;
        while (read < count)
        {
            int current = stream.Read(buffer, read, count - read);
            if (current <= 0)
                throw new PkgFormatException($"Unexpected end of source while reading {what}.");
            read += current;
        }
    }
}
