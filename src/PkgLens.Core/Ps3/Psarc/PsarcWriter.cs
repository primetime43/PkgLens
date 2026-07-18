using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PkgLens.Core.Ps3.Psarc;

public static class PsarcWriter
{
    private const int HeaderSize = 0x20;
    private const int TocEntrySize = 0x1E;
    private const ulong MaxUInt40 = 0xFF_FFFF_FFFF;

    public static void Repack(string sourcePath, PsarcArchiveInfo archive,
        IReadOnlyDictionary<PsarcEntry, PsarcReplacement> replacements, string destinationPath,
        CancellationToken cancellationToken = default, IProgress<PsarcProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string sourceFullPath = Path.GetFullPath(sourcePath);
        string destinationFullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a different output path; PSARC rebuilding never overwrites the source archive.");
        if (replacements.Keys.Any(entry => !archive.Entries.Contains(entry)))
            throw new ArgumentException("A replacement does not belong to this PSARC archive.", nameof(replacements));
        if (replacements.Values.Any(replacement => replacement.Length < 0))
            throw new ArgumentException("Replacement lengths cannot be negative.", nameof(replacements));

        Directory.CreateDirectory(Path.GetDirectoryName(destinationFullPath)!);
        string outputTemporary = destinationFullPath + $".{Guid.NewGuid():N}.pkglens-part";
        try
        {
            BuildArchive(sourceFullPath, archive, replacements, outputTemporary,
                cancellationToken, progress);
            File.Move(outputTemporary, destinationFullPath, true);
        }
        catch
        {
            if (File.Exists(outputTemporary)) File.Delete(outputTemporary);
            throw;
        }
    }

    private static void BuildArchive(string sourcePath, PsarcArchiveInfo archive,
        IReadOnlyDictionary<PsarcEntry, PsarcReplacement> replacements, string destinationTemporary,
        CancellationToken cancellationToken, IProgress<PsarcProgress>? progress)
    {
        PsarcHeader original = archive.Header;
        uint blockSize = original.BlockSize;
        int blockLengthSize = PsarcReader.GetBlockLengthSize(blockSize);
        long total = archive.Entries.Sum(entry => replacements.TryGetValue(entry, out var replacement)
            ? replacement.Length
            : checked((long)entry.Length));
        long completed = 0;

        string manifestText = string.Join('\n', archive.Entries.Select(entry => entry.Path)) + '\n';
        byte[] manifestBytes = new UTF8Encoding(false).GetBytes(manifestText);
        long expectedBlockCount = PsarcReader.GetEntryBlockCount((ulong)manifestBytes.LongLength, blockSize);
        foreach (PsarcEntry entry in archive.Entries)
        {
            long length = replacements.TryGetValue(entry, out PsarcReplacement? replacement)
                ? replacement.Length
                : checked((long)entry.Length);
            expectedBlockCount = checked(expectedBlockCount + PsarcReader.GetEntryBlockCount((ulong)length, blockSize));
        }
        if (expectedBlockCount > int.MaxValue)
            throw new InvalidDataException("The rebuilt PSARC block table is too large.");

        uint rebuiltEntryCount = checked((uint)archive.Entries.Count + 1);
        ulong tocBytes = checked((ulong)TocEntrySize * rebuiltEntryCount);
        ulong dataOffset = checked((ulong)HeaderSize + tocBytes + (ulong)expectedBlockCount * (uint)blockLengthSize);
        if (dataOffset > uint.MaxValue)
            throw new InvalidDataException("The rebuilt PSARC table exceeds the format's 32-bit data offset.");

        var staged = new List<StagedEntry>(archive.Entries.Count + 1);
        var allBlockLengths = new List<uint>(checked((int)expectedBlockCount));

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        uint[] sourceBlockLengths = PsarcReader.ReadBlockLengths(source, original);
        using var output = new FileStream(destinationTemporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1024 * 1024, FileOptions.SequentialScan);
        output.Position = checked((long)dataOffset);

        using (var manifest = new MemoryStream(manifestBytes, writable: false))
            staged.Add(StageEntry(manifest, manifestBytes.LongLength, new byte[16], output, blockSize,
                original.Compression, allBlockLengths, cancellationToken, null, null));

        foreach (PsarcEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] digest = ComputeNameDigest(entry.Path, original.Flags);
            if (replacements.TryGetValue(entry, out PsarcReplacement? replacement))
            {
                using Stream input = replacement.OpenRead();
                if (!input.CanRead) throw new InvalidOperationException($"The replacement stream for {entry.Path} is not readable.");
                long itemBase = completed;
                var itemProgress = new PsarcProgressRelay(value =>
                    progress?.Report(new PsarcProgress(itemBase + value.Completed, total, entry.Path)));
                staged.Add(StageEntry(input, replacement.Length, digest, output, blockSize, original.Compression,
                    allBlockLengths, cancellationToken, itemProgress, entry.Path));
                if (input.ReadByte() != -1)
                    throw new InvalidDataException($"The replacement for {entry.Path} is longer than its declared length.");
                completed += replacement.Length;
            }
            else
            {
                using var plain = new PsarcEntryReadStream(source, archive, entry, sourceBlockLengths,
                    cancellationToken);
                long itemBase = completed;
                var itemProgress = new PsarcProgressRelay(value =>
                    progress?.Report(new PsarcProgress(itemBase + value.Completed, total, entry.Path)));
                staged.Add(StageEntry(plain, checked((long)entry.Length), digest, output, blockSize,
                    original.Compression, allBlockLengths, cancellationToken, itemProgress, entry.Path));
                completed += checked((long)entry.Length);
            }
            progress?.Report(new PsarcProgress(completed, total, entry.Path));
        }

        if (allBlockLengths.Count != expectedBlockCount)
            throw new InvalidDataException("The rebuilt PSARC block count changed while streaming input.");
        output.Position = 0;
        WriteHeader(output, original, checked((uint)dataOffset), rebuiltEntryCount);
        foreach (StagedEntry entry in staged) WriteTocEntry(output, entry);
        foreach (uint length in allBlockLengths) WriteBlockLength(output, length, blockLengthSize);
        output.Flush(flushToDisk: true);
    }

    private static StagedEntry StageEntry(Stream input, long length, byte[] digest, Stream payload, uint blockSize,
        string compression, List<uint> allBlockLengths, CancellationToken cancellationToken,
        IProgress<PsarcProgress>? progress, string? item)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        long payloadStart = payload.Position;
        long remaining = length;
        long completed = 0;
        int firstBlock = allBlockLengths.Count;
        byte[] plain = new byte[checked((int)blockSize)];
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int logicalSize = (int)Math.Min(blockSize, remaining);
            ReadExactly(input, plain.AsSpan(0, logicalSize), item);
            byte[] compressed = PsarcBlockCodec.Encode(compression, plain, logicalSize, blockSize);

            if (compressed.Length < logicalSize && compressed.Length < blockSize)
            {
                allBlockLengths.Add(checked((uint)compressed.Length));
                payload.Write(compressed);
            }
            else
            {
                allBlockLengths.Add(0);
                payload.Write(plain, 0, logicalSize);
            }
            remaining -= logicalSize;
            completed += logicalSize;
            progress?.Report(new PsarcProgress(completed, length, item));
        }

        if ((ulong)length > MaxUInt40 || (ulong)payloadStart > MaxUInt40)
            throw new InvalidDataException("A rebuilt PSARC entry exceeds the format's 40-bit size or offset limit.");
        return new StagedEntry(digest, checked((uint)firstBlock), length, checked((ulong)payloadStart),
            payload.Position - payloadStart,
            allBlockLengths.Count - firstBlock);
    }

    private static byte[] ComputeNameDigest(string path, PsarcArchiveFlags flags)
    {
        string digestPath = (flags & PsarcArchiveFlags.IgnoreCase) != 0 ? path.ToLowerInvariant() : path;
        return MD5.HashData(Encoding.UTF8.GetBytes(digestPath));
    }

    private static void WriteHeader(Stream output, PsarcHeader original, uint dataOffset, uint entryCount)
    {
        Span<byte> bytes = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x50534152);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[4..], original.MajorVersion);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[6..], original.MinorVersion);
        if (!PsarcBlockCodec.IsSupported(original.Compression) || original.Compression.Length != 4)
            throw new NotSupportedException($"PSARC compression '{original.Compression}' is not supported.");
        Encoding.ASCII.GetBytes(original.Compression, bytes[8..12]);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[12..], dataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[16..], TocEntrySize);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[20..], entryCount);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[24..], original.BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[28..], (uint)original.Flags);
        output.Write(bytes);
    }

    private static void WriteTocEntry(Stream output, StagedEntry entry)
    {
        Span<byte> bytes = stackalloc byte[TocEntrySize];
        entry.Digest.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[16..], entry.FirstBlockIndex);
        WriteUInt40(bytes[20..25], checked((ulong)entry.Length));
        WriteUInt40(bytes[25..30], entry.DataOffset);
        output.Write(bytes);
    }

    private static void WriteBlockLength(Stream output, uint length, int size)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, length);
        output.Write(bytes[(4 - size)..]);
    }

    private static void WriteUInt40(Span<byte> bytes, ulong value)
    {
        if (value > MaxUInt40) throw new ArgumentOutOfRangeException(nameof(value));
        bytes[0] = (byte)(value >> 32);
        bytes[1] = (byte)(value >> 24);
        bytes[2] = (byte)(value >> 16);
        bytes[3] = (byte)(value >> 8);
        bytes[4] = (byte)value;
    }

    private static void ReadExactly(Stream input, Span<byte> destination, string? item)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = input.Read(destination[offset..]);
            if (read == 0)
                throw new EndOfStreamException($"The replacement stream for {item ?? "an archive entry"} ended before its declared length.");
            offset += read;
        }
    }

    private sealed record StagedEntry(byte[] Digest, uint FirstBlockIndex, long Length, ulong DataOffset,
        long StoredLength, int BlockCount);

    private sealed class PsarcEntryReadStream : Stream
    {
        private readonly Stream _source;
        private readonly PsarcEntry _entry;
        private readonly uint[] _blockLengths;
        private readonly uint _blockSize;
        private readonly string _compression;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _storedBuffer;
        private readonly byte[] _plainBuffer;
        private int _blockIndex;
        private int _plainOffset;
        private int _plainCount;
        private ulong _remaining;
        private long _position;

        public PsarcEntryReadStream(Stream source, PsarcArchiveInfo archive, PsarcEntry entry,
            uint[] blockLengths, CancellationToken cancellationToken)
        {
            _source = source;
            _entry = entry;
            _blockLengths = blockLengths;
            _blockSize = archive.Header.BlockSize;
            _compression = archive.Header.Compression;
            _cancellationToken = cancellationToken;
            _storedBuffer = new byte[checked((int)_blockSize)];
            _plainBuffer = new byte[checked((int)_blockSize)];
            _remaining = entry.Length;
            _source.Position = checked((long)entry.DataOffset);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => checked((long)_entry.Length);
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0 || _remaining == 0 && _plainOffset == _plainCount) return 0;
            int written = 0;
            while (written < buffer.Length)
            {
                if (_plainOffset == _plainCount && !LoadBlock()) break;
                int copy = Math.Min(buffer.Length - written, _plainCount - _plainOffset);
                _plainBuffer.AsSpan(_plainOffset, copy).CopyTo(buffer[written..]);
                _plainOffset += copy;
                written += copy;
                _position += copy;
            }
            return written;
        }

        private bool LoadBlock()
        {
            if (_remaining == 0) return false;
            _cancellationToken.ThrowIfCancellationRequested();
            int logicalSize = checked((int)Math.Min((ulong)_blockSize, _remaining));
            int tableIndex = checked((int)_entry.FirstBlockIndex + _blockIndex);
            uint marker = _blockLengths[tableIndex];
            int storedSize = marker == 0 ? logicalSize : checked((int)marker);
            PsarcReader.ReadExactly(_source, _storedBuffer.AsSpan(0, storedSize));

            PsarcBlockCodec.Decode(_compression, _storedBuffer, storedSize, _plainBuffer, logicalSize, _blockSize,
                _entry.Path);

            _blockIndex++;
            _remaining -= (uint)logicalSize;
            _plainOffset = 0;
            _plainCount = logicalSize;
            return true;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
