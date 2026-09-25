using System.Buffers.Binary;
using System.Security.Cryptography;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public sealed record PkgFinalizationReport(string ContentId, int FileCount, int DirectoryCount,
    long SourceSize, long OutputSize, long DataSize, string PlaintextSha256);

/// <summary>Streams debug PS3 packages into unsigned retail encryption, preserving the entire plaintext data region.</summary>
public static class PkgFinalizer
{
    private const int HeaderSize = 0xC0;
    private const int FooterSize = 0x80;
    private const int BufferSize = 1024 * 1024;

    /// <summary>Validates the on-disk input without decrypting embedded executable or license contents.</summary>
    public static PkgInfo Inspect(Stream source, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek || !source.CanRead) throw new ArgumentException("A readable, seekable package is required.");
        token.ThrowIfCancellationRequested();
        source.Position = 0;
        byte[] head = new byte[HeaderSize]; source.ReadExactly(head);
        var header = PkgHeader.Parse(head);
        if (!header.IsPs3) throw new PkgFormatException("Finalize PKG supports PS3 packages only.");
        if (header.Finalization == PkgFinalization.Retail)
            throw new PkgFormatException("This package is already retail-encrypted. No finalization is needed.");
        if (header.Finalization != PkgFinalization.Debug)
            throw new PkgFormatException("Only non-finalized (debug) packages can be finalized.");
        if (header.TotalSize != (ulong)source.Length || header.DataOffset < HeaderSize
            || header.DataOffset > header.TotalSize || header.DataSize > header.TotalSize - header.DataOffset
            || header.MetadataOffset < HeaderSize || (ulong)header.MetadataOffset + header.MetadataSize > header.DataOffset)
            throw new PkgFormatException("Package size or data/metadata bounds are invalid. Split or truncated packages must be repaired first.");
        ulong trailer = header.TotalSize - header.DataOffset - header.DataSize;
        if (trailer is not 0 and not FooterSize)
            throw new PkgFormatException("Unsupported package trailer layout. Expected no footer or a standard 128-byte footer.");
        if (trailer == FooterSize)
        {
            source.Position = source.Length - 0x20;
            byte[] stored = new byte[20]; source.ReadExactly(stored);
            if (stored.Any(b => b != 0))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                byte[] buffer = new byte[BufferSize];
                source.Position = 0;
                for (long left = source.Length - 0x20; left > 0;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, left);
                    source.ReadExactly(buffer, 0, count); hash.AppendData(buffer, 0, count); left -= count;
                }
                if (!hash.GetHashAndReset().AsSpan().SequenceEqual(stored))
                    throw new PkgFormatException("Input package footer checksum mismatch. The source may be damaged.");
            }
        }
        var keys = new InMemoryKeyProvider();
        var verification = PkgVerifier.Verify(source, keys);
        // A debug package's old signature is not an assertion of retail authenticity.
        var failure = verification.Checks.FirstOrDefault(c => c.Status == PkgCheckStatus.Fail && c.Name != "Header ECDSA");
        if (failure is not null) throw new PkgFormatException($"Input check failed ({failure.Name}): {failure.Detail}");
        var info = PkgReader.Read(source, keys);
        if (!info.IsDecrypted) throw new PkgFormatException("Cannot read the debug package item table.");
        token.ThrowIfCancellationRequested();
        return info;
    }

    /// <summary>Writes a new package. Caller owns atomic publication and calls Verify before publishing.</summary>
    public static PkgFinalizationReport Convert(Stream source, Stream destination, IKeyProvider retailKeys,
        CancellationToken token = default, IProgress<PkgOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(retailKeys);
        if (ReferenceEquals(source, destination)) throw new ArgumentException("Source and output must be separate streams.");
        if (!destination.CanWrite) throw new ArgumentException("A writable output stream is required.");
        if (destination.CanSeek && destination.Position != 0) throw new ArgumentException("Output must start at offset zero.");
        var info = Inspect(source, token);
        var header = info.Header;
        long dataOffset = checked((long)header.DataOffset), dataSize = checked((long)header.DataSize);
        long outputSize = checked(dataOffset + dataSize + FooterSize);
        source.Position = 0;
        byte[] head = new byte[HeaderSize]; source.ReadExactly(head);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(4), 0x8000);
        BinaryPrimitives.WriteUInt64BigEndian(head.AsSpan(0x18), (ulong)outputSize);
        head.AsSpan(0x80, 0x40).Clear();
        var targetHeader = PkgHeader.Parse(head);
        if (!retailKeys.TryResolve(targetHeader, out var retail, out string? reason)
            || retail.Scheme != PkgFinalization.Retail || !retail.TryGetHeaderCmacKey(out byte[] macKey))
            throw new PkgKeyException(reason ?? "A PS3 retail package key is required to finalize.");
        AesCmac.Compute(macKey, head.AsSpan(0, 0x80)).CopyTo(head.AsSpan(0x80));
        SHA1.HashData(head.AsSpan(0, 0x80)).AsSpan(12, 8).CopyTo(head.AsSpan(0xB8));
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var encryptors = retail.CreateDecryptorSet();
        var debug = new DebugSha1Decryptor(header.QaDigest);
        byte[] buffer = new byte[BufferSize];
        token.ThrowIfCancellationRequested();
        WriteHashed(destination, head, fileHash);
        // Keep metadata, padding and any extended header bytes at their original offsets.
        Copy(source, destination, dataOffset - HeaderSize, buffer, token, fileHash);
        for (long offset = 0; offset < dataSize;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, dataSize - offset);
            source.ReadExactly(buffer, 0, count);
            var bytes = buffer.AsSpan(0, count);
            debug.DecryptInPlace(bytes, offset);
            plainHash.AppendData(bytes);
            encryptors.Table.DecryptInPlace(bytes, offset);
            WriteHashed(destination, bytes, fileHash);
            offset += count;
            progress?.Report(new(offset, dataSize, "Encrypting package"));
        }
        // Standard pseudo-retail footer: cleared authentication area followed by a SHA-1 checksum.
        // This is a container checksum, not a Sony ECDSA signature.
        WriteHashed(destination, new byte[FooterSize - 0x20], fileHash);
        byte[] footer = new byte[0x20];
        fileHash.GetHashAndReset().CopyTo(footer, 0);
        token.ThrowIfCancellationRequested();
        destination.Write(footer);
        if (destination.CanSeek) destination.SetLength(outputSize);
        token.ThrowIfCancellationRequested();
        return new(header.ContentId.Raw, info.FileCount, info.DirectoryCount, source.Length, outputSize,
            dataSize, System.Convert.ToHexString(plainHash.GetHashAndReset()));
    }

    /// <summary>Checks retail structure, header MAC, whole-package checksum and exact plaintext preservation.</summary>
    public static void Verify(Stream output, IKeyProvider retailKeys, PkgFinalizationReport expected,
        CancellationToken token = default, IProgress<PkgOperationProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var report = PkgVerifier.Verify(output, retailKeys);
        if (!report.Passed) throw new PkgFormatException("Finalized package verification failed: " +
            string.Join("; ", report.Checks.Where(c => c.Status == PkgCheckStatus.Fail).Select(c => c.Name + ": " + c.Detail)));
        if (!report.Checks.Any(c => c.Name == "Header CMAC" && c.Status == PkgCheckStatus.Pass)
            || !report.Checks.Any(c => c.Name == "Item table" && c.Status == PkgCheckStatus.Pass))
            throw new PkgFormatException("Finalized package header MAC and contents must be verified.");
        var info = PkgReader.Read(output, retailKeys);
        if (info.Header.Finalization != PkgFinalization.Retail || !info.Header.IsPs3
            || output.Length != expected.OutputSize || info.Header.TotalSize != (ulong)expected.OutputSize
            || info.Header.ContentId.Raw != expected.ContentId || info.FileCount != expected.FileCount
            || info.DirectoryCount != expected.DirectoryCount || info.Header.DataSize != (ulong)expected.DataSize
            || info.Header.DataOffset + info.Header.DataSize + FooterSize != (ulong)expected.OutputSize)
            throw new PkgFormatException("Finalized package layout does not match the source.");
        if (!retailKeys.TryResolve(info.Header, out var context, out string? reason))
            throw new PkgKeyException(reason ?? "Cannot verify retail output.");
        using var decryptors = context.CreateDecryptorSet();
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[BufferSize];
        long dataStart = (long)info.Header.DataOffset;
        long dataEnd = dataStart + expected.DataSize;
        long hashEnd = output.Length - 0x20;
        output.Position = 0;
        for (long position = 0; position < hashEnd;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, hashEnd - position);
            output.ReadExactly(buffer, 0, count);
            var bytes = buffer.AsSpan(0, count);
            fileHash.AppendData(bytes);
            long begin = Math.Max(position, dataStart), end = Math.Min(position + count, dataEnd);
            if (end > begin)
            {
                var data = bytes.Slice((int)(begin - position), (int)(end - begin));
                decryptors.Table.DecryptInPlace(data, begin - dataStart);
                plainHash.AppendData(data);
            }
            position += count;
            progress?.Report(new(position, hashEnd, "Verifying contents and checksum"));
        }
        byte[] footer = new byte[0x20]; output.ReadExactly(footer);
        if (!fileHash.GetHashAndReset().AsSpan().SequenceEqual(footer.AsSpan(0, 20)))
            throw new PkgFormatException("Finalized package footer checksum mismatch.");
        if (!System.Convert.ToHexString(plainHash.GetHashAndReset()).Equals(expected.PlaintextSha256, StringComparison.Ordinal))
            throw new PkgFormatException("Finalized package contents differ from the source.");
        token.ThrowIfCancellationRequested();
    }

    private static void WriteHashed(Stream destination, ReadOnlySpan<byte> bytes, IncrementalHash hash)
    { hash.AppendData(bytes); destination.Write(bytes); }

    private static void Copy(Stream source, Stream destination, long count, byte[] buffer,
        CancellationToken token, IncrementalHash hash)
    {
        while (count > 0)
        {
            token.ThrowIfCancellationRequested();
            int next = (int)Math.Min(buffer.Length, count);
            source.ReadExactly(buffer, 0, next);
            WriteHashed(destination, buffer.AsSpan(0, next), hash);
            count -= next;
        }
    }
}
