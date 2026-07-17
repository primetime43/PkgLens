using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Psp;

public enum PspExportFormat
{
    Pbp,
    Iso,
    Cso,
}

public sealed record PspExportProgress(string Stage, double Percentage);

public sealed record PspExportEligibility(bool CanExport, string Reason, string? PackageEntry = null);

public sealed record PspExportResult(
    PspExportFormat Format,
    string ContentId,
    string PackageEntry,
    long OutputSize,
    string? DiscId = null,
    long? IsoSize = null);

/// <summary>Exports a PSP package directly to its EBOOT.PBP, decrypted ISO, or CSO image.</summary>
public static class PspPackageExporter
{
    public static PspExportEligibility CheckEligibility(Stream package, IKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(keys);
        if (!package.CanRead || !package.CanSeek)
            throw new ArgumentException("Package input must be readable and seekable.", nameof(package));
        return CheckEligibility(PkgReader.Read(package, keys));
    }

    public static PspExportEligibility CheckEligibility(PkgInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.Header.IsPspPsVita)
            return new(false, $"This is a {info.Header.PlatformDisplay} package, not a PSP package.");
        if (info.Header.PspKeyType != 1)
            return new(false, $"This is a {info.Header.PlatformDisplay} package, not a PSP package.");
        if (!info.IsDecrypted)
            return new(false, info.DecryptionNote ?? "The PSP package could not be decrypted.");
        if (!TryFindEboot(info.Entries, out PkgEntry? eboot, out string reason))
            return new(false, reason);
        return new(true, "PSP package with EBOOT.PBP detected.", eboot!.Name);
    }

    public static PspExportResult Export(Stream package, Stream destination, IKeyProvider keys,
        PspExportFormat format, string? temporaryDirectory = null,
        CancellationToken cancellationToken = default, IProgress<PspExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(keys);
        if (!package.CanRead || !package.CanSeek)
            throw new ArgumentException("Package input must be readable and seekable.", nameof(package));
        if (!destination.CanWrite)
            throw new ArgumentException("Export destination must be writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new("Reading package", 0));
        PkgInfo info = PkgReader.Read(package, keys);
        PspExportEligibility eligibility = CheckEligibility(info);
        if (!eligibility.CanExport)
        {
            if (info.Header.IsPspPsVita && info.Header.PspKeyType == 1 && !info.IsDecrypted)
                throw new PkgKeyException(eligibility.Reason);
            throw new PkgFormatException(eligibility.Reason);
        }

        PkgEntry eboot = info.Entries.Single(entry => entry.Name == eligibility.PackageEntry);
        string contentId = info.ContentId.Raw;
        if (format == PspExportFormat.Pbp)
        {
            progress?.Report(new("Extracting EBOOT.PBP", 5));
            var copyProgress = progress is null ? null : new Progress<long>(bytes =>
                progress.Report(new("Extracting EBOOT.PBP", 5 + bytes * 95d / Math.Max(1d, eboot.FileSize))));
            PkgReader.ExtractEntry(package, info.Header, eboot, destination, keys,
                cancellationToken, copyProgress);
            progress?.Report(new("Complete", 100));
            return new(format, contentId, eboot.Name, checked((long)eboot.FileSize));
        }

        string tempRoot = temporaryDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(tempRoot);
        string tempPbp = Path.Combine(tempRoot, $".pkglens-psp-{Guid.NewGuid():N}.pbp");
        try
        {
            progress?.Report(new("Extracting EBOOT.PBP", 2));
            using (var pbpOutput = new FileStream(tempPbp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var extractProgress = progress is null ? null : new Progress<long>(bytes =>
                    progress.Report(new("Extracting EBOOT.PBP", 2 + bytes * 28d / Math.Max(1d, eboot.FileSize))));
                PkgReader.ExtractEntry(package, info.Header, eboot, pbpOutput, keys,
                    cancellationToken, extractProgress);
            }

            using var pbp = new FileStream(tempPbp, FileMode.Open, FileAccess.Read, FileShare.Read);
            PbpArchive archive = PbpArchive.Parse(pbp);
            PbpEntry psar = archive.Entries.SingleOrDefault(entry => entry.Name == "DATA.PSAR")
                ?? throw new PkgFormatException("EBOOT.PBP does not contain a DATA.PSAR disc image.");
            using var psarStream = new SegmentStream(pbp, psar.Offset, psar.Size);

            var header = new byte[0x100];
            psarStream.ReadExactly(header);
            NpumdImgInfo image = NpumdImg.ParseHeader(header);
            if (!image.HeaderValid)
                throw new PkgKeyException("NPUMDIMG header did not decrypt to a valid layout (corrupt or unsupported).");
            long isoSize = checked(image.TotalSectors * image.SectorSize);
            psarStream.Position = 0;

            var decryptProgress = progress is null ? null : new Progress<double>(percentage =>
                progress.Report(new(format == PspExportFormat.Cso ? "Decrypting and compressing disc" : "Decrypting disc",
                    30 + percentage * 0.70)));

            if (format == PspExportFormat.Iso)
            {
                NpumdImg.DecryptToIso(psarStream, destination, cancellationToken, decryptProgress);
            }
            else if (format == PspExportFormat.Cso)
            {
                Stream cso = CsoWriter.Create(destination, isoSize, leaveOpen: true);
                try
                {
                    NpumdImg.DecryptToIso(psarStream, cso, cancellationToken, decryptProgress);
                    cso.Dispose();
                }
                catch
                {
                    CsoWriter.Abort(cso);
                    throw;
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(format));
            }

            progress?.Report(new("Complete", 100));
            long outputSize = destination.CanSeek ? destination.Length : isoSize;
            return new(format, contentId, eboot.Name, outputSize, image.DiscId, isoSize);
        }
        finally
        {
            try { File.Delete(tempPbp); } catch { /* best-effort temporary-file cleanup */ }
        }
    }

    private static bool TryFindEboot(IReadOnlyList<PkgEntry> entries, out PkgEntry? eboot, out string reason)
    {
        var matches = entries.Where(entry => entry.IsFile &&
            Path.GetFileName(entry.Name).Equals("EBOOT.PBP", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            eboot = null;
            reason = "This PSP package does not contain an EBOOT.PBP file.";
            return false;
        }

        PkgEntry? standard = matches.FirstOrDefault(entry =>
            entry.Name.Replace('\\', '/').Equals("USRDIR/CONTENT/EBOOT.PBP", StringComparison.OrdinalIgnoreCase));
        if (standard is not null)
        {
            eboot = standard;
            reason = string.Empty;
            return true;
        }
        if (matches.Length == 1)
        {
            eboot = matches[0];
            reason = string.Empty;
            return true;
        }
        eboot = null;
        reason = "This PSP package contains multiple EBOOT.PBP files and no standard USRDIR/CONTENT/EBOOT.PBP entry.";
        return false;
    }

    private sealed class SegmentStream : Stream
    {
        private readonly Stream _source;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SegmentStream(Stream source, long start, long length)
        {
            _source = source;
            _start = start;
            _length = length;
        }

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

        public override int Read(byte[] buffer, int offset, int count)
        {
            int wanted = (int)Math.Min(count, _length - _position);
            if (wanted <= 0) return 0;
            _source.Position = _start + _position;
            int read = _source.Read(buffer, offset, wanted);
            _position += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int wanted = (int)Math.Min(buffer.Length, _length - _position);
            if (wanted <= 0) return 0;
            _source.Position = _start + _position;
            int read = _source.Read(buffer[..wanted]);
            _position += read;
            return read;
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

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
