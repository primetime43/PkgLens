using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Crypto;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;
using PkgLens.Core.Sfo;

namespace PkgLens.Core.Formats.Ps3;

/// <summary>
/// Reads a PS3 PKG from a seekable stream. Streaming and memory-light: only the header, the
/// metadata block, the item table, and the (small) entry-name and PARAM.SFO regions are read —
/// never the bulk file data. Actual file extraction decrypts entry ranges lazily elsewhere.
/// </summary>
public sealed class Ps3PackageReader : IPackageReader
{
    /// <summary>Guard against absurd allocations from a corrupt count/size field.</summary>
    private const long MaxReasonableRegion = 256L * 1024 * 1024;

    public bool CanRead(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 4) return false;
        long pos = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> magic = stackalloc byte[4];
            if (stream.Read(magic) != 4) return false;
            return BinaryPrimitives.ReadUInt32BigEndian(magic) == PkgHeader.Magic;
        }
        finally
        {
            stream.Position = pos;
        }
    }

    public PkgInfo Read(Stream stream, IKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(keys);
        if (!stream.CanSeek)
            throw new PkgFormatException("A seekable stream is required to read a PKG.");

        // 1. Header.
        var headerBytes = ReadAt(stream, 0, PkgHeader.MinLength, "header");
        var header = PkgHeader.Parse(headerBytes);

        if (!header.IsPs3)
            throw new PkgFormatException(
                $"Not a PS3 package (platform raw 0x{header.RawPlatform:X4}). PSP/PSVita is not yet supported.");

        // 2. Metadata block.
        var metadata = ReadMetadata(stream, header);

        // 3. Resolve a decryptor (always succeeds for debug; retail needs a key).
        if (!keys.TryResolve(header, out var context, out string? reason))
        {
            return new PkgInfo
            {
                Header = header,
                Metadata = metadata,
                IsDecrypted = false,
                DecryptionNote = reason,
            };
        }

        var decryptor = context.CreateDecryptor();
        try
        {
            // 4. Item table + names.
            var entries = ReadEntries(stream, header, decryptor);

            // 5. PARAM.SFO enrichment.
            var sfo = ReadSfo(stream, header, decryptor, entries);

            return new PkgInfo
            {
                Header = header,
                Metadata = metadata,
                Entries = entries,
                Sfo = sfo,
                IsDecrypted = true,
            };
        }
        finally
        {
            (decryptor as IDisposable)?.Dispose();
        }
    }

    private static PkgMetadata ReadMetadata(Stream stream, PkgHeader header)
    {
        if (header.MetadataCount == 0 || header.MetadataSize == 0)
            return new PkgMetadata(Array.Empty<PkgMetadataEntry>());

        if (header.MetadataSize > MaxReasonableRegion)
            throw new PkgFormatException($"Metadata size 0x{header.MetadataSize:X} is implausibly large.");

        var block = ReadAt(stream, header.MetadataOffset, (int)header.MetadataSize, "metadata block");
        return PkgMetadata.Parse(block, header.MetadataCount);
    }

    private static List<PkgEntry> ReadEntries(Stream stream, PkgHeader header, IPkgDecryptor decryptor)
    {
        long tableBytes = (long)header.ItemCount * PkgEntry.RecordSize;
        if (tableBytes == 0)
            return new List<PkgEntry>();
        if (tableBytes > MaxReasonableRegion)
            throw new PkgFormatException($"Item table ({tableBytes} bytes) is implausibly large.");

        // The item table lives at the very start of the encrypted data region (region offset 0).
        byte[] table = ReadDataRegion(stream, header, regionOffset: 0, length: (int)tableBytes);
        decryptor.DecryptInPlace(table, 0);

        var entries = new List<PkgEntry>((int)header.ItemCount);
        for (int i = 0; i < header.ItemCount; i++)
        {
            var rec = PkgEntry.ParseRecord(table.AsSpan(i * PkgEntry.RecordSize, PkgEntry.RecordSize));
            entries.Add(rec);
        }

        // Entry names sit contiguously between the item table and the file data. Read that whole
        // span once and decrypt it, then slice each name out (avoids one seek per entry).
        long nameStart = long.MaxValue, nameEnd = 0;
        foreach (var e in entries)
        {
            if (e.NameSize == 0) continue;
            nameStart = Math.Min(nameStart, e.NameOffset);
            nameEnd = Math.Max(nameEnd, (long)e.NameOffset + e.NameSize);
        }

        if (nameEnd > nameStart)
        {
            long span = nameEnd - nameStart;
            if (span > MaxReasonableRegion)
                throw new PkgFormatException($"Entry-name region ({span} bytes) is implausibly large.");
            if (nameEnd > (long)header.DataSize)
                throw new PkgFormatException("An entry name extends beyond the data region.");

            byte[] names = ReadDataRegion(stream, header, nameStart, (int)span);
            decryptor.DecryptInPlace(names, nameStart);

            foreach (var e in entries)
            {
                if (e.NameSize == 0) continue;
                int rel = (int)(e.NameOffset - nameStart);
                e.Name = Encoding.UTF8.GetString(names, rel, (int)e.NameSize).TrimEnd('\0');
            }
        }

        return entries;
    }

    private static SfoTable? ReadSfo(Stream stream, PkgHeader header, IPkgDecryptor decryptor, List<PkgEntry> entries)
    {
        var sfoEntry = entries.FirstOrDefault(e =>
            e.IsFile && e.FileSize > 0 &&
            (e.Name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) ||
             e.Name.EndsWith("/PARAM.SFO", StringComparison.OrdinalIgnoreCase)));

        if (sfoEntry is null)
            return null;
        if (sfoEntry.FileSize > MaxReasonableRegion)
            return null;

        byte[] data = ReadDataRegion(stream, header, (long)sfoEntry.FileOffset, (int)sfoEntry.FileSize);
        decryptor.DecryptInPlace(data, (long)sfoEntry.FileOffset);

        try
        {
            return SfoParser.Parse(data);
        }
        catch (PkgFormatException)
        {
            // A malformed SFO should not fail the whole read; the rest of the model is still useful.
            return null;
        }
    }

    /// <summary>
    /// Decrypts a single entry's data into memory. Suitable for small entries (PARAM.SFO,
    /// ICON0.PNG). For large files prefer <see cref="CopyEntryTo"/> to avoid buffering the whole file.
    /// </summary>
    public static byte[] ReadEntry(Stream stream, PkgHeader header, IPkgDecryptor decryptor, PkgEntry entry)
    {
        if (entry.IsDirectory || entry.FileSize == 0)
            return Array.Empty<byte>();
        if (entry.FileSize > int.MaxValue)
            throw new PkgFormatException(
                $"Entry '{entry.Name}' ({entry.FileSize} bytes) is too large to buffer; use CopyEntryTo.");

        byte[] data = ReadDataRegion(stream, header, (long)entry.FileOffset, (int)entry.FileSize);
        decryptor.DecryptInPlace(data, (long)entry.FileOffset);
        return data;
    }

    /// <summary>Streams an entry's decrypted data to <paramref name="destination"/> without buffering it whole.</summary>
    public static void CopyEntryTo(Stream stream, PkgHeader header, IPkgDecryptor decryptor, PkgEntry entry,
        Stream destination, int bufferSize = 1 << 20)
    {
        if (entry.IsDirectory || entry.FileSize == 0)
            return;

        long regionOffset = (long)entry.FileOffset;
        long remaining = (long)entry.FileSize;

        if (regionOffset < 0 || regionOffset + remaining > (long)header.DataSize)
            throw new PkgFormatException($"Entry '{entry.Name}' data range exceeds data_size.");

        long absoluteBase = (long)header.DataOffset + regionOffset;
        if (absoluteBase < 0 || absoluteBase + remaining > stream.Length)
            throw new PkgFormatException($"Truncated PKG: entry '{entry.Name}' data extends past end of file.");

        var buffer = new byte[(int)Math.Min(bufferSize, remaining)];
        stream.Position = absoluteBase;
        long pos = regionOffset;

        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = 0;
            while (read < want)
            {
                int n = stream.Read(buffer, read, want - read);
                if (n <= 0)
                    throw new PkgFormatException($"Truncated PKG: unexpected end while reading '{entry.Name}'.");
                read += n;
            }

            decryptor.DecryptInPlace(buffer.AsSpan(0, want), pos);
            destination.Write(buffer, 0, want);
            pos += want;
            remaining -= want;
        }
    }

    /// <summary>Reads <paramref name="length"/> bytes from the encrypted data region at <paramref name="regionOffset"/>.</summary>
    private static byte[] ReadDataRegion(Stream stream, PkgHeader header, long regionOffset, int length)
    {
        long absolute = (long)header.DataOffset + regionOffset;
        if (regionOffset < 0 || (long)regionOffset + length > (long)header.DataSize)
            throw new PkgFormatException(
                $"Data-region read [0x{regionOffset:X}, +0x{length:X}) exceeds data_size 0x{header.DataSize:X}.");
        return ReadAt(stream, absolute, length, "data region");
    }

    /// <summary>Reads exactly <paramref name="length"/> bytes at absolute <paramref name="offset"/>.</summary>
    private static byte[] ReadAt(Stream stream, long offset, int length, string what)
    {
        if (offset < 0 || offset + length > stream.Length)
            throw new PkgFormatException(
                $"Truncated PKG: {what} read [0x{offset:X}, +0x{length:X}) exceeds file length 0x{stream.Length:X}.");

        stream.Position = offset;
        var buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(buffer, read, length - read);
            if (n <= 0)
                throw new PkgFormatException($"Truncated PKG: unexpected end while reading {what}.");
            read += n;
        }
        return buffer;
    }
}
