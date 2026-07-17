using System.Buffers.Binary;
using System.IO.Compression;

namespace PkgLens.Core.Psp;

/// <summary>Writes PSP-compatible CISO/CSO v1 images using 2 KiB raw-DEFLATE blocks.</summary>
public static class CsoWriter
{
    public const int BlockSize = 0x800;

    /// <summary>
    /// Creates a write-only stream that converts a sequential ISO into CSO v1 data. The destination
    /// must be writable and seekable because the block index is finalized after the last sector.
    /// </summary>
    public static Stream Create(Stream destination, long isoSize, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return new CsoCompressionStream(destination, isoSize, leaveOpen);
    }

    /// <summary>Compresses a seekable or sequential ISO stream into a PSP-compatible CSO image.</summary>
    public static void Compress(Stream source, Stream destination, long isoSize,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Stream compressor = Create(destination, isoSize, leaveOpen: true);
        try
        {
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            while (copied < isoSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(buffer.Length, isoSize - copied);
                int read = source.Read(buffer, 0, wanted);
                if (read <= 0)
                    throw new EndOfStreamException($"ISO ended after {copied:n0} of {isoSize:n0} bytes.");
                compressor.Write(buffer, 0, read);
                copied += read;
                progress?.Report(copied * 100d / isoSize);
            }
            compressor.Dispose();
        }
        catch
        {
            Abort(compressor);
            throw;
        }
    }

    internal static void Abort(Stream stream)
    {
        if (stream is CsoCompressionStream compressor)
            compressor.Abort();
        else
            stream.Dispose();
    }

    private sealed class CsoCompressionStream : Stream
    {
        private const int HeaderSize = 0x18;
        private const uint PlainBlockFlag = 0x80000000;

        private readonly Stream _destination;
        private readonly bool _leaveOpen;
        private readonly long _isoSize;
        private readonly uint[] _index;
        private readonly byte[] _sector = new byte[BlockSize];
        private readonly MemoryStream _compressed = new(BlockSize);
        private readonly int _alignment;
        private int _sectorLength;
        private int _blockIndex;
        private long _written;
        private bool _disposed;
        private bool _aborted;

        public CsoCompressionStream(Stream destination, long isoSize, bool leaveOpen)
        {
            if (!destination.CanWrite || !destination.CanSeek)
                throw new ArgumentException("CSO output must be writable and seekable.", nameof(destination));
            if (isoSize <= 0 || isoSize % BlockSize != 0)
                throw new ArgumentOutOfRangeException(nameof(isoSize),
                    $"ISO size must be a positive multiple of {BlockSize} bytes.");

            long blockCount = isoSize / BlockSize;
            if (blockCount >= int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(isoSize), "ISO has too many sectors for a CSO v1 index.");

            _destination = destination;
            _leaveOpen = leaveOpen;
            _isoSize = isoSize;
            _index = new uint[checked((int)blockCount + 1)];
            _alignment = SelectAlignment(isoSize, blockCount);

            destination.Position = 0;
            destination.SetLength(0);
            long tableEnd = checked(HeaderSize + (long)_index.Length * sizeof(uint));
            destination.SetLength(tableEnd);
            destination.Position = tableEnd;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => _written;
        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _destination.Flush();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count) throw new ArgumentException("Offset and count exceed the buffer.");
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.Length > _isoSize - _written)
                throw new InvalidOperationException($"CSO input exceeds the declared ISO size of {_isoSize:n0} bytes.");

            while (!buffer.IsEmpty)
            {
                int take = Math.Min(BlockSize - _sectorLength, buffer.Length);
                buffer[..take].CopyTo(_sector.AsSpan(_sectorLength));
                _sectorLength += take;
                _written += take;
                buffer = buffer[take..];
                if (_sectorLength == BlockSize)
                    WriteSector();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                try
                {
                    if (!_aborted)
                        Complete();
                }
                finally
                {
                    _disposed = true;
                    _compressed.Dispose();
                    if (!_leaveOpen) _destination.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        public void Abort()
        {
            _aborted = true;
            Dispose();
        }

        private void WriteSector()
        {
            AlignDestination();
            uint offset = CheckedIndexOffset(_destination.Position);

            _compressed.Position = 0;
            _compressed.SetLength(0);
            using (var deflate = new DeflateStream(_compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(_sector);

            if (_compressed.Length < BlockSize)
            {
                _index[_blockIndex] = offset;
                _compressed.Position = 0;
                _compressed.CopyTo(_destination);
            }
            else
            {
                _index[_blockIndex] = offset | PlainBlockFlag;
                _destination.Write(_sector);
            }

            _blockIndex++;
            _sectorLength = 0;
        }

        private void Complete()
        {
            if (_written != _isoSize || _sectorLength != 0 || _blockIndex != _index.Length - 1)
                throw new InvalidOperationException(
                    $"CSO input ended after {_written:n0} of {_isoSize:n0} bytes.");

            AlignDestination();
            _index[^1] = CheckedIndexOffset(_destination.Position);
            long end = _destination.Position;

            Span<byte> header = stackalloc byte[HeaderSize];
            "CISO"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], HeaderSize);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], checked((ulong)_isoSize));
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x10..], BlockSize);
            header[0x14] = 1;
            header[0x15] = checked((byte)_alignment);

            _destination.Position = 0;
            _destination.Write(header);
            Span<byte> value = stackalloc byte[sizeof(uint)];
            foreach (uint entry in _index)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, entry);
                _destination.Write(value);
            }
            _destination.Position = end;
            _destination.SetLength(end);
            _destination.Flush();
        }

        private void AlignDestination()
        {
            long mask = (1L << _alignment) - 1;
            int padding = checked((int)((-_destination.Position) & mask));
            if (padding > 0)
                _destination.Write(new byte[padding]);
        }

        private uint CheckedIndexOffset(long position)
        {
            long shifted = position >> _alignment;
            if (shifted > 0x7FFFFFFF)
                throw new InvalidOperationException("CSO v1 output exceeded its 31-bit block index.");
            return checked((uint)shifted);
        }

        private static int SelectAlignment(long isoSize, long blockCount)
        {
            for (int alignment = 0; alignment <= 31; alignment++)
            {
                long padding = (1L << alignment) - 1;
                long maximum;
                try
                {
                    maximum = checked(HeaderSize + (blockCount + 1) * sizeof(uint) + isoSize + blockCount * padding);
                }
                catch (OverflowException)
                {
                    continue;
                }
                if ((maximum >> alignment) <= 0x7FFFFFFF)
                    return alignment;
            }
            throw new ArgumentOutOfRangeException(nameof(isoSize), "ISO is too large for CSO v1.");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
