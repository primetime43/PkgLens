using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public enum PostVerificationStatus { Pass, Warning, Fail }

public sealed record PostVerificationCheck(string Name, PostVerificationStatus Status, string Detail);

public sealed class PostOperationVerificationReport
{
    public PostOperationVerificationReport(string outputPath, IReadOnlyList<PostVerificationCheck> checks)
    {
        OutputPath = outputPath;
        Checks = checks;
    }

    public string OutputPath { get; }
    public IReadOnlyList<PostVerificationCheck> Checks { get; }
    public bool Passed => Checks.All(check => check.Status != PostVerificationStatus.Fail);
    public int Warnings => Checks.Count(check => check.Status == PostVerificationStatus.Warning);

    public void EnsurePassed()
    {
        PostVerificationCheck? failure = Checks.FirstOrDefault(check => check.Status == PostVerificationStatus.Fail);
        if (failure is not null)
            throw new PkgFormatException($"Post-operation verification failed ({failure.Name}): {failure.Detail}");
    }
}

/// <summary>Reopens and independently validates files produced by write operations.</summary>
public static class PostOperationVerifier
{
    private const uint CsoPlainBlockFlag = 0x80000000;
    private const uint CsoOffsetMask = 0x7FFFFFFF;

    public static PostOperationVerificationReport VerifyPackage(string outputPath, IKeyProvider keys,
        IEnumerable<string>? embeddedFakeSelfPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(keys);

        using var source = File.OpenRead(outputPath);
        PkgVerificationReport packageReport = PkgVerifier.Verify(source, keys);
        var checks = new List<PostVerificationCheck>(packageReport.Checks.Count);
        foreach (PkgCheck check in packageReport.Checks)
        {
            bool expectedUnsignedAuthentication = check.Status == PkgCheckStatus.Fail &&
                check.Name is "Header CMAC" or "Header ECDSA";
            PostVerificationStatus status = expectedUnsignedAuthentication
                ? PostVerificationStatus.Warning
                : check.Status switch
                {
                    PkgCheckStatus.Pass => PostVerificationStatus.Pass,
                    PkgCheckStatus.Skipped => PostVerificationStatus.Warning,
                    _ => PostVerificationStatus.Fail,
                };
            checks.Add(new PostVerificationCheck(check.Name, status, check.Detail));
        }

        if (!checks.Any(check => check.Name == "Item table" && check.Status == PostVerificationStatus.Pass))
            checks.Add(new PostVerificationCheck("Readable contents", PostVerificationStatus.Fail,
                "The rebuilt package item table could not be decrypted and validated."));

        if (embeddedFakeSelfPaths is not null && !checks.Any(check => check.Status == PostVerificationStatus.Fail))
        {
            source.Position = 0;
            PkgInfo info = PkgReader.Read(source, keys);
            foreach (string path in embeddedFakeSelfPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                PkgEntry? entry = info.Entries.FirstOrDefault(candidate => candidate.IsFile &&
                    candidate.Name.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    checks.Add(new PostVerificationCheck($"Fake SELF: {path}", PostVerificationStatus.Fail,
                        "The converted executable is missing from the rebuilt package."));
                    continue;
                }

                byte[] self = PkgReader.ExtractEntryBytes(source, info.Header, entry, keys);
                VerifyFakeSelfBytes(self, null, $"Fake SELF: {path}", checks);
            }
        }

        return Complete(outputPath, checks);
    }

    public static PostOperationVerificationReport VerifyDecryptedData(
        string sourcePath, string outputPath, byte[]? klicensee = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var checks = new List<PostVerificationCheck>();
        byte[] outputHash;
        long outputLength;
        using (var output = File.OpenRead(outputPath))
        {
            outputLength = output.Length;
            outputHash = SHA256.HashData(output);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var input = File.OpenRead(sourcePath);
        using var sink = new HashingSinkStream(cancellationToken);
        if (PspEdatFile.IsPspEncrypted(input))
            PspEdatFile.Decrypt(input, sink);
        else
            EdatFile.Decrypt(input, sink, klicensee);

        byte[] verificationHash = sink.GetHashAndReset();
        checks.Add(outputLength == sink.BytesWritten
            ? new PostVerificationCheck("Plaintext size", PostVerificationStatus.Pass,
                $"{outputLength:n0} bytes match an independent decryption pass")
            : new PostVerificationCheck("Plaintext size", PostVerificationStatus.Fail,
                $"output has {outputLength:n0} bytes; independent decryption produced {sink.BytesWritten:n0}"));
        checks.Add(CryptographicOperations.FixedTimeEquals(outputHash, verificationHash)
            ? new PostVerificationCheck("Plaintext SHA-256", PostVerificationStatus.Pass,
                Convert.ToHexString(outputHash).ToLowerInvariant())
            : new PostVerificationCheck("Plaintext SHA-256", PostVerificationStatus.Fail,
                "The saved output differs from an independent authenticated decryption pass."));

        return Complete(outputPath, checks);
    }

    public static PostOperationVerificationReport VerifyPspExport(
        string outputPath, PspExportResult result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(result);

        using var source = File.OpenRead(outputPath);
        var checks = new List<PostVerificationCheck>();
        checks.Add(source.Length == result.OutputSize
            ? new PostVerificationCheck("Output size", PostVerificationStatus.Pass, $"{source.Length:n0} bytes")
            : new PostVerificationCheck("Output size", PostVerificationStatus.Fail,
                $"expected {result.OutputSize:n0} bytes, found {source.Length:n0}"));

        switch (result.Format)
        {
            case PspExportFormat.Pbp:
                PbpArchive archive = PbpArchive.Parse(source);
                checks.Add(new PostVerificationCheck("PBP structure", PostVerificationStatus.Pass,
                    $"{archive.Entries.Count} non-empty section(s)"));
                break;
            case PspExportFormat.Iso:
                VerifyIso(source, result.IsoSize, checks);
                break;
            case PspExportFormat.Cso:
                VerifyCso(source, result.IsoSize, checks, cancellationToken);
                break;
            default:
                checks.Add(new PostVerificationCheck("Format", PostVerificationStatus.Fail,
                    $"Unsupported PSP export format {result.Format}."));
                break;
        }

        return Complete(outputPath, checks);
    }

    public static PostOperationVerificationReport VerifyIso(
        string outputPath, long? expectedSize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        using var source = File.OpenRead(outputPath);
        var checks = new List<PostVerificationCheck>();
        VerifyIso(source, expectedSize, checks);
        return Complete(outputPath, checks);
    }

    public static PostOperationVerificationReport VerifyFakeSelf(
        string outputPath, byte[]? expectedElf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        byte[] self = File.ReadAllBytes(outputPath);
        var checks = new List<PostVerificationCheck>();

        VerifyFakeSelfBytes(self, expectedElf, "SELF", checks);
        return Complete(outputPath, checks);
    }

    private static void VerifyFakeSelfBytes(byte[] self, byte[]? expectedElf,
        string checkPrefix, List<PostVerificationCheck> checks)
    {

        SelfInfo info;
        try
        {
            using var stream = new MemoryStream(self, writable: false);
            info = SelfReader.ParseInfo(stream);
            checks.Add(new PostVerificationCheck($"{checkPrefix} structure", PostVerificationStatus.Pass,
                $"key revision 0x{info.KeyRevision:X4}, {info.Segments.Count} segment(s)"));
        }
        catch (Exception ex) when (ex is PkgFormatException or EndOfStreamException)
        {
            checks.Add(new PostVerificationCheck($"{checkPrefix} structure", PostVerificationStatus.Fail, ex.Message));
            return;
        }

        checks.Add(info.IsLikelyFakeSigned
            ? new PostVerificationCheck($"{checkPrefix} marker", PostVerificationStatus.Pass, "key revision is 0x8000")
            : new PostVerificationCheck($"{checkPrefix} marker", PostVerificationStatus.Fail,
                $"key revision is 0x{info.KeyRevision:X4}, not 0x8000"));

        try
        {
            SelfDecryptResult decrypted = SelfDecryptor.Decrypt(self);
            bool elfMagic = decrypted.Elf.Length >= 4 && decrypted.Elf[0] == 0x7F &&
                decrypted.Elf[1] == (byte)'E' && decrypted.Elf[2] == (byte)'L' &&
                decrypted.Elf[3] == (byte)'F';
            checks.Add(elfMagic
                ? new PostVerificationCheck($"{checkPrefix} ELF round trip", PostVerificationStatus.Pass,
                    $"decrypted to {decrypted.Elf.Length:n0} bytes")
                : new PostVerificationCheck($"{checkPrefix} ELF round trip", PostVerificationStatus.Fail,
                    "The fake-signed output did not decrypt to an ELF."));

            if (expectedElf is not null)
            {
                bool same = CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(expectedElf), SHA256.HashData(decrypted.Elf));
                checks.Add(same
                    ? new PostVerificationCheck($"{checkPrefix} ELF content", PostVerificationStatus.Pass,
                        "The decrypted fake SELF matches the source ELF.")
                    : new PostVerificationCheck($"{checkPrefix} ELF content", PostVerificationStatus.Fail,
                        "The decrypted fake SELF does not match the source ELF."));
            }
        }
        catch (Exception ex) when (ex is PkgFormatException or PkgKeyException or CryptographicException)
        {
            checks.Add(new PostVerificationCheck($"{checkPrefix} ELF round trip", PostVerificationStatus.Fail, ex.Message));
        }
    }

    private static PostOperationVerificationReport Complete(
        string outputPath, List<PostVerificationCheck> checks)
    {
        var report = new PostOperationVerificationReport(outputPath, checks);
        report.EnsurePassed();
        return report;
    }

    private static void VerifyIso(Stream source, long? expectedSize, List<PostVerificationCheck> checks)
    {
        long length = source.Length;
        if (expectedSize is not null)
        {
            checks.Add(length == expectedSize.Value
                ? new PostVerificationCheck("ISO size", PostVerificationStatus.Pass, $"{length:n0} bytes")
                : new PostVerificationCheck("ISO size", PostVerificationStatus.Fail,
                    $"expected {expectedSize.Value:n0} bytes, found {length:n0}"));
        }

        checks.Add(length > 0 && length % CsoWriter.BlockSize == 0
            ? new PostVerificationCheck("Sector alignment", PostVerificationStatus.Pass,
                $"{length / CsoWriter.BlockSize:n0} sectors")
            : new PostVerificationCheck("Sector alignment", PostVerificationStatus.Fail,
                $"ISO length {length:n0} is not a positive multiple of {CsoWriter.BlockSize}."));

        if (length < 17L * CsoWriter.BlockSize)
        {
            checks.Add(new PostVerificationCheck("ISO9660 descriptor", PostVerificationStatus.Fail,
                "The image is too small to contain sector 16."));
            return;
        }

        Span<byte> descriptor = stackalloc byte[7];
        source.Position = 16L * CsoWriter.BlockSize;
        source.ReadExactly(descriptor);
        bool valid = descriptor[0] == 1 && descriptor[1..6].SequenceEqual("CD001"u8) && descriptor[6] == 1;
        checks.Add(valid
            ? new PostVerificationCheck("ISO9660 descriptor", PostVerificationStatus.Pass,
                "Primary volume descriptor found at sector 16")
            : new PostVerificationCheck("ISO9660 descriptor", PostVerificationStatus.Fail,
                "Sector 16 does not contain a valid ISO9660 primary volume descriptor."));
    }

    private static void VerifyCso(Stream source, long? expectedIsoSize,
        List<PostVerificationCheck> checks, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[0x18];
        if (source.Length < header.Length)
            throw new PkgFormatException("Post-operation verification failed (CSO header): output is truncated.");
        source.Position = 0;
        source.ReadExactly(header);
        if (!header[..4].SequenceEqual("CISO"u8))
            throw new PkgFormatException("Post-operation verification failed (CSO header): CISO magic is missing.");

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x04..]);
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(header[0x08..]);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x10..]);
        byte version = header[0x14];
        int alignment = header[0x15];
        bool headerValid = headerSize == 0x18 && blockSize == CsoWriter.BlockSize && version == 1 &&
            declaredSize > 0 && declaredSize % blockSize == 0 && alignment <= 20;
        checks.Add(headerValid
            ? new PostVerificationCheck("CSO header", PostVerificationStatus.Pass,
                $"v{version}, {declaredSize:n0} uncompressed bytes")
            : new PostVerificationCheck("CSO header", PostVerificationStatus.Fail,
                "Header size, version, block size, image size, or alignment is invalid."));
        if (!headerValid) return;

        if (expectedIsoSize is not null)
        {
            checks.Add(declaredSize == (ulong)expectedIsoSize.Value
                ? new PostVerificationCheck("CSO image size", PostVerificationStatus.Pass,
                    $"{declaredSize:n0} bytes")
                : new PostVerificationCheck("CSO image size", PostVerificationStatus.Fail,
                    $"expected {expectedIsoSize.Value:n0} bytes, header declares {declaredSize:n0}"));
        }

        long blockCount64 = checked((long)(declaredSize / blockSize));
        if (blockCount64 >= int.MaxValue)
        {
            checks.Add(new PostVerificationCheck("CSO index", PostVerificationStatus.Fail, "Too many blocks."));
            return;
        }
        int blockCount = (int)blockCount64;
        long tableEnd = checked((long)headerSize + ((long)blockCount + 1) * sizeof(uint));
        if (tableEnd > source.Length)
        {
            checks.Add(new PostVerificationCheck("CSO index", PostVerificationStatus.Fail,
                "The block index extends beyond the file."));
            return;
        }

        var index = new uint[blockCount + 1];
        Span<byte> entryBytes = stackalloc byte[4];
        source.Position = headerSize;
        for (int i = 0; i < index.Length; i++)
        {
            source.ReadExactly(entryBytes);
            index[i] = BinaryPrimitives.ReadUInt32LittleEndian(entryBytes);
        }

        byte[] sector = new byte[blockSize];
        byte[]? sector16 = null;
        for (int i = 0; i < blockCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long start = checked((long)(index[i] & CsoOffsetMask) << alignment);
            long end = checked((long)(index[i + 1] & CsoOffsetMask) << alignment);
            if (start < tableEnd || end <= start || end > source.Length)
            {
                checks.Add(new PostVerificationCheck("CSO index", PostVerificationStatus.Fail,
                    $"Block {i} has an invalid span [0x{start:X}, 0x{end:X})."));
                return;
            }

            Array.Clear(sector);
            source.Position = start;
            if ((index[i] & CsoPlainBlockFlag) != 0)
            {
                if (end - start < blockSize)
                {
                    checks.Add(new PostVerificationCheck("CSO blocks", PostVerificationStatus.Fail,
                        $"Plain block {i} is truncated."));
                    return;
                }
                source.ReadExactly(sector);
            }
            else
            {
                long compressedLength = end - start;
                long maximumLength = blockSize + ((1L << alignment) - 1);
                if (compressedLength > maximumLength)
                {
                    checks.Add(new PostVerificationCheck("CSO blocks", PostVerificationStatus.Fail,
                        $"Compressed block {i} is implausibly large."));
                    return;
                }
                using var segment = new ReadOnlySegmentStream(source, start, compressedLength);
                using var deflate = new DeflateStream(segment, CompressionMode.Decompress);
                int read = 0;
                while (read < sector.Length)
                {
                    int count = deflate.Read(sector, read, sector.Length - read);
                    if (count == 0) break;
                    read += count;
                }
                if (read != sector.Length || deflate.ReadByte() != -1)
                {
                    checks.Add(new PostVerificationCheck("CSO blocks", PostVerificationStatus.Fail,
                        $"Compressed block {i} does not expand to exactly {blockSize} bytes."));
                    return;
                }
            }

            if (i == 16) sector16 = (byte[])sector.Clone();
        }

        checks.Add(new PostVerificationCheck("CSO index", PostVerificationStatus.Pass,
            $"{blockCount:n0} monotonic block entries"));
        checks.Add(new PostVerificationCheck("CSO blocks", PostVerificationStatus.Pass,
            $"all {blockCount:n0} blocks decoded successfully"));
        bool isoDescriptor = sector16 is { Length: >= 7 } && sector16[0] == 1 &&
            sector16.AsSpan(1, 5).SequenceEqual("CD001"u8) && sector16[6] == 1;
        checks.Add(isoDescriptor
            ? new PostVerificationCheck("ISO9660 descriptor", PostVerificationStatus.Pass,
                "Primary volume descriptor recovered from sector 16")
            : new PostVerificationCheck("ISO9660 descriptor", PostVerificationStatus.Fail,
                "Decompressed sector 16 is not a valid ISO9660 primary volume descriptor."));
    }

    private sealed class HashingSinkStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly CancellationToken _cancellationToken;

        public HashingSinkStream(CancellationToken cancellationToken) => _cancellationToken = cancellationToken;
        public long BytesWritten { get; private set; }
        public byte[] GetHashAndReset() => _hash.GetHashAndReset();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _hash.AppendData(buffer);
            BytesWritten += buffer.Length;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ReadOnlySegmentStream : Stream
    {
        private readonly Stream _source;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public ReadOnlySegmentStream(Stream source, long start, long length)
        {
            _source = source;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int wanted = (int)Math.Min(buffer.Length, _length - _position);
            if (wanted <= 0) return 0;
            _source.Position = _start + _position;
            int read = _source.Read(buffer[..wanted]);
            _position += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
