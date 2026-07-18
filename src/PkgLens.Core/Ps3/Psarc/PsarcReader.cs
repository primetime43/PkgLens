using System.Buffers.Binary;
using System.Text;

namespace PkgLens.Core.Ps3.Psarc;

public static class PsarcReader
{
    private const uint Magic = 0x50534152;
    private const int HeaderSize = 0x20;
    private const int StandardTocEntrySize = 0x1E;
    private const uint MaxBlockSize = 16 * 1024 * 1024;
    private const ulong MaxManifestSize = 64 * 1024 * 1024;

    public static PsarcArchiveInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream);
    }

    public static PsarcArchiveInfo Read(Stream stream)
    {
        ValidateReadableSeekable(stream);
        if (stream.Length < HeaderSize)
            throw new InvalidDataException("The file is too small to be a PSARC archive.");

        stream.Position = 0;
        Span<byte> headerBytes = stackalloc byte[HeaderSize];
        ReadExactly(stream, headerBytes);
        if (BinaryPrimitives.ReadUInt32BigEndian(headerBytes) != Magic)
            throw new InvalidDataException("The file does not have a PSARC header.");

        ushort major = BinaryPrimitives.ReadUInt16BigEndian(headerBytes[4..]);
        ushort minor = BinaryPrimitives.ReadUInt16BigEndian(headerBytes[6..]);
        if (major != 1 || minor is < 3 or > 4)
            throw new NotSupportedException($"PSARC version {major}.{minor} is not supported; versions 1.3 and 1.4 are supported.");

        string compression = Encoding.ASCII.GetString(headerBytes[8..12]);
        if (!PsarcBlockCodec.IsSupported(compression))
            throw new NotSupportedException($"PSARC compression '{compression}' is not supported. Supported compression types are zlib and LZMA.");

        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[12..]);
        uint tocEntrySize = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[16..]);
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[20..]);
        uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[24..]);
        var flags = (PsarcArchiveFlags)BinaryPrimitives.ReadUInt32BigEndian(headerBytes[28..]);

        if ((flags & PsarcArchiveFlags.Encrypted) != 0)
            throw new NotSupportedException("Encrypted or PSARC-MSELF hybrid archives are not supported.");
        if (tocEntrySize < StandardTocEntrySize || tocEntrySize > 4096)
            throw new InvalidDataException($"Invalid PSARC TOC entry size: {tocEntrySize}.");
        if (entryCount == 0 || entryCount > 10_000_000)
            throw new InvalidDataException($"Invalid PSARC entry count: {entryCount}.");
        if (blockSize == 0 || blockSize > MaxBlockSize)
            throw new InvalidDataException($"Invalid PSARC block size: {blockSize}.");

        int blockLengthSize = GetBlockLengthSize(blockSize);
        ulong tocEnd = HeaderSize + (ulong)tocEntrySize * entryCount;
        if (dataOffset < tocEnd || dataOffset > (ulong)stream.Length)
            throw new InvalidDataException("The PSARC data offset is outside the archive.");
        ulong blockTableBytes = dataOffset - tocEnd;
        if (blockTableBytes % (uint)blockLengthSize != 0)
            throw new InvalidDataException("The PSARC block-length table is not aligned.");
        ulong blockCount64 = blockTableBytes / (uint)blockLengthSize;
        if (blockCount64 > int.MaxValue)
            throw new InvalidDataException("The PSARC block table is too large.");

        var records = new RawEntry[checked((int)entryCount)];
        stream.Position = HeaderSize;
        byte[] tocBuffer = new byte[tocEntrySize];
        for (int index = 0; index < records.Length; index++)
        {
            ReadExactly(stream, tocBuffer);
            records[index] = new RawEntry(
                tocBuffer[..16].ToArray(),
                BinaryPrimitives.ReadUInt32BigEndian(tocBuffer.AsSpan(16, 4)),
                ReadUInt40(tocBuffer.AsSpan(20, 5)),
                ReadUInt40(tocBuffer.AsSpan(25, 5)));
        }

        var blockLengths = new uint[(int)blockCount64];
        Span<byte> lengthBytes = stackalloc byte[4];
        for (int index = 0; index < blockLengths.Length; index++)
        {
            lengthBytes.Clear();
            ReadExactly(stream, lengthBytes[(4 - blockLengthSize)..]);
            blockLengths[index] = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        }

        ValidateRecords(records, blockLengths, blockSize, stream.Length);
        if (records[0].Length > MaxManifestSize)
            throw new InvalidDataException("The PSARC manifest is too large to inspect safely.");

        using var manifest = new MemoryStream(checked((int)records[0].Length));
        ExtractRawEntry(stream, records[0], blockLengths, blockSize, compression, manifest,
            CancellationToken.None, null, null);
        string manifestText = new UTF8Encoding(false, true).GetString(manifest.ToArray()).TrimEnd('\0');
        string[] names = manifestText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        while (names.Length > 0 && names[^1].Length == 0)
            Array.Resize(ref names, names.Length - 1);
        if (names.Length != records.Length - 1)
            throw new InvalidDataException($"The PSARC manifest lists {names.Length} files, but the TOC contains {records.Length - 1} file entries.");

        var entries = new List<PsarcEntry>(names.Length);
        var paths = new HashSet<string>((flags & PsarcArchiveFlags.IgnoreCase) != 0
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        for (int index = 1; index < records.Length; index++)
        {
            string path = ValidateArchivePath(names[index - 1], flags);
            if (!paths.Add(path))
                throw new InvalidDataException($"The PSARC manifest contains a duplicate path: {path}");
            RawEntry record = records[index];
            int count = GetEntryBlockCount(record.Length, blockSize);
            ulong storedLength = GetStoredLength(record, blockLengths, blockSize);
            entries.Add(new PsarcEntry(index - 1, path, record.Digest, record.FirstBlockIndex,
                record.Length, record.Offset, storedLength, count));
        }

        var header = new PsarcHeader(major, minor, compression, dataOffset, tocEntrySize, entryCount,
            blockSize, flags, blockLengthSize);
        return new PsarcArchiveInfo(header, entries);
    }

    public static void ExtractEntry(Stream source, PsarcArchiveInfo archive, PsarcEntry entry, Stream destination,
        CancellationToken cancellationToken = default, IProgress<PsarcProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entry);
        ValidateReadableSeekable(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        var raw = new RawEntry(entry.NameDigest, entry.FirstBlockIndex, entry.Length, entry.DataOffset);
        uint[] lengths = ReadBlockLengths(source, archive.Header);
        ExtractRawEntry(source, raw, lengths, archive.Header.BlockSize, archive.Header.Compression,
            destination, cancellationToken, progress, entry.Path);
    }

    public static byte[] ExtractEntryBytes(Stream source, PsarcArchiveInfo archive, PsarcEntry entry,
        int maximumBytes = 64 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (entry.Length > (ulong)maximumBytes)
            throw new InvalidOperationException($"{entry.Path} is too large to preview ({entry.Length:n0} bytes). Extract it to disk instead.");
        using var output = new MemoryStream(checked((int)entry.Length));
        ExtractEntry(source, archive, entry, output, cancellationToken);
        return output.ToArray();
    }

    public static void ExtractAll(string sourcePath, PsarcArchiveInfo archive, string destinationDirectory,
        CancellationToken cancellationToken = default, IProgress<PsarcProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        long total = archive.Entries.Sum(entry => checked((long)entry.Length));
        long completed = 0;
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        uint[] lengths = ReadBlockLengths(source, archive.Header);

        foreach (PsarcEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = entry.Path.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            string outputPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!outputPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive path escapes the extraction folder: {entry.Path}");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            string temporary = outputPath + ".pkglens-part";
            try
            {
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var raw = new RawEntry(entry.NameDigest, entry.FirstBlockIndex, entry.Length, entry.DataOffset);
                    var itemProgress = new PsarcProgressRelay(value =>
                        progress?.Report(new PsarcProgress(completed + value.Completed, total, entry.Path)));
                    ExtractRawEntry(source, raw, lengths, archive.Header.BlockSize, archive.Header.Compression,
                        output, cancellationToken, itemProgress, entry.Path);
                }
                File.Move(temporary, outputPath, true);
                completed += checked((long)entry.Length);
                progress?.Report(new PsarcProgress(completed, total, entry.Path));
            }
            catch
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }
        }
    }

    internal static uint[] ReadBlockLengths(Stream source, PsarcHeader header)
    {
        ulong tocEnd = HeaderSize + (ulong)header.TocEntrySize * header.EntryCount;
        int count = checked((int)((header.DataOffset - tocEnd) / (uint)header.BlockLengthSize));
        var lengths = new uint[count];
        source.Position = checked((long)tocEnd);
        Span<byte> bytes = stackalloc byte[4];
        for (int index = 0; index < count; index++)
        {
            bytes.Clear();
            ReadExactly(source, bytes[(4 - header.BlockLengthSize)..]);
            lengths[index] = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }
        return lengths;
    }

    internal static void ExtractRawEntry(Stream source, RawEntry entry, uint[] blockLengths, uint blockSize,
        string compression, Stream destination, CancellationToken cancellationToken,
        IProgress<PsarcProgress>? progress, string? item)
    {
        int count = GetEntryBlockCount(entry.Length, blockSize);
        ulong remaining = entry.Length;
        long completed = 0;
        source.Position = checked((long)entry.Offset);
        byte[] compressed = new byte[checked((int)blockSize)];
        byte[] plain = new byte[checked((int)blockSize)];

        for (int block = 0; block < count; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int logicalSize = checked((int)Math.Min((ulong)blockSize, remaining));
            int tableIndex = checked((int)entry.FirstBlockIndex + block);
            uint storedMarker = blockLengths[tableIndex];
            int storedSize = storedMarker == 0 ? logicalSize : checked((int)storedMarker);
            ReadExactly(source, compressed.AsSpan(0, storedSize));

            PsarcBlockCodec.Decode(compression, compressed, storedSize, plain, logicalSize, blockSize, item);
            destination.Write(plain, 0, logicalSize);

            remaining -= (uint)logicalSize;
            completed += logicalSize;
            progress?.Report(new PsarcProgress(completed, checked((long)entry.Length), item));
        }
    }

    internal static int GetEntryBlockCount(ulong length, uint blockSize) =>
        length == 0 ? 0 : checked((int)((length + blockSize - 1) / blockSize));

    internal static int GetBlockLengthSize(uint blockSize)
    {
        uint maximumValue = blockSize - 1;
        if (maximumValue <= byte.MaxValue) return 1;
        if (maximumValue <= ushort.MaxValue) return 2;
        if (maximumValue <= 0xFFFFFF) return 3;
        return 4;
    }

    internal static ulong ReadUInt40(ReadOnlySpan<byte> bytes) =>
        ((ulong)bytes[0] << 32) | ((ulong)bytes[1] << 24) | ((ulong)bytes[2] << 16) |
        ((ulong)bytes[3] << 8) | bytes[4];

    internal static string ValidateArchivePath(string path, PsarcArchiveFlags flags)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            throw new InvalidDataException("The PSARC manifest contains an empty or invalid path.");
        string normalized = path.Replace('\\', '/');
        bool absolute = (flags & PsarcArchiveFlags.AbsolutePaths) != 0;
        if (absolute != normalized.StartsWith('/'))
            throw new InvalidDataException($"The PSARC path does not match the archive path mode: {path}");
        string relative = normalized.TrimStart('/');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException($"The PSARC manifest contains an unsafe path: {path}");
        foreach (string component in relative.Split('/'))
        {
            if (component.Length == 0 || component is "." or "..")
                throw new InvalidDataException($"The PSARC manifest contains an unsafe path: {path}");
        }
        return normalized;
    }

    private static void ValidateRecords(RawEntry[] records, uint[] blockLengths, uint blockSize, long streamLength)
    {
        foreach (RawEntry entry in records)
        {
            int count = GetEntryBlockCount(entry.Length, blockSize);
            if ((ulong)entry.FirstBlockIndex + (uint)count > (ulong)blockLengths.Length)
                throw new InvalidDataException("A PSARC entry references blocks outside the block table.");
            ulong stored = GetStoredLength(entry, blockLengths, blockSize);
            if (entry.Offset > (ulong)streamLength || stored > (ulong)streamLength - entry.Offset)
                throw new InvalidDataException("A PSARC entry's data lies outside the archive.");
            for (int block = 0; block < count; block++)
            {
                uint marker = blockLengths[checked((int)entry.FirstBlockIndex + block)];
                if (marker > blockSize)
                    throw new InvalidDataException("A PSARC compressed block is larger than the configured block size.");
            }
        }
    }

    private static ulong GetStoredLength(RawEntry entry, uint[] blockLengths, uint blockSize)
    {
        ulong remaining = entry.Length;
        ulong stored = 0;
        int count = GetEntryBlockCount(entry.Length, blockSize);
        for (int block = 0; block < count; block++)
        {
            uint logical = (uint)Math.Min((ulong)blockSize, remaining);
            uint marker = blockLengths[checked((int)entry.FirstBlockIndex + block)];
            stored += marker == 0 ? logical : marker;
            remaining -= logical;
        }
        return stored;
    }

    private static void ValidateReadableSeekable(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The PSARC stream must be readable and seekable.", nameof(stream));
    }

    internal static void ReadExactly(Stream stream, Span<byte> destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = stream.Read(destination[offset..]);
            if (read == 0) throw new EndOfStreamException("Unexpected end of PSARC archive.");
            offset += read;
        }
    }

    internal sealed record RawEntry(byte[] Digest, uint FirstBlockIndex, ulong Length, ulong Offset);
}
