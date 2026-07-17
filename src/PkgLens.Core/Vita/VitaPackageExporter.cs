using System.Text;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Vita;

public enum VitaPackageKind
{
    App,
    Update,
    Dlc,
    Theme,
}

public enum VitaLicenseStatus
{
    NotRequired,
    Missing,
    Included,
}

public sealed record VitaPackageDetails(
    VitaPackageKind Kind,
    string ContentId,
    string TitleId,
    string Title,
    string? Category,
    string? AppVersion,
    string? MinimumFirmware,
    byte KeyRevision,
    uint? DrmType,
    string RelativeRoot,
    bool RequiresLicense);

public sealed record VitaExportProgress(string Stage, double Percentage);

public sealed record VitaExportResult(
    VitaPackageDetails Package,
    string OutputRoot,
    int FileCount,
    long ExtractedBytes,
    VitaLicenseStatus LicenseStatus,
    IReadOnlyList<string> Warnings);

public static class VitaPackageExporter
{
    private const uint VitaAppContentType = 0x15;
    private const uint VitaDlcContentType = 0x16;
    private const uint VitaThemeContentType = 0x1F;
    private const int WorkBinSize = 512;

    public static VitaPackageDetails Inspect(Stream package, IKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(keys);
        return Inspect(PkgReader.Read(package, keys));
    }

    public static VitaPackageDetails Inspect(PkgInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.Header.IsPspPsVita || info.Header.PspKeyType is < 2 or > 4)
            throw new PkgFormatException(
                $"Vita export requires a PSVita package using key revision 2, 3, or 4; this package is {info.Header.PlatformDisplay} with key revision {info.Header.PspKeyType}.");
        if (!info.IsDecrypted)
            throw new PkgKeyException(info.DecryptionNote ?? "The Vita package could not be decrypted.");

        uint contentType = info.Metadata.ContentTypeRaw ?? throw new PkgFormatException(
            "Vita package metadata does not contain a content type.");
        string? category = info.Sfo?.Category;
        VitaPackageKind kind = contentType switch
        {
            VitaAppContentType when category?.Equals("gp", StringComparison.OrdinalIgnoreCase) == true => VitaPackageKind.Update,
            VitaAppContentType => VitaPackageKind.App,
            VitaDlcContentType => VitaPackageKind.Dlc,
            VitaThemeContentType => VitaPackageKind.Theme,
            _ => throw new PkgFormatException($"Unsupported Vita content type 0x{contentType:X}; expected app/update (0x15), DLC (0x16), or theme (0x1F)."),
        };

        string contentId = info.Sfo?.GetString("CONTENT_ID") ?? info.ContentId.Raw;
        ContentId parsed = ContentId.Parse(contentId);
        string titleId = info.Sfo?.TitleId ?? parsed.TitleId ?? throw new PkgFormatException(
            "Vita package has no TITLE_ID in PARAM.SFO or content ID.");
        if (titleId.Length != 9)
            throw new PkgFormatException($"Vita TITLE_ID must be 9 characters, got '{titleId}'.");

        string relativeRoot = kind switch
        {
            VitaPackageKind.App => $"app/{titleId}",
            VitaPackageKind.Update => $"patch/{titleId}",
            VitaPackageKind.Dlc => $"addcont/{titleId}/{DlcDirectory(parsed, contentId)}",
            VitaPackageKind.Theme => $"app/{titleId}",
            _ => throw new ArgumentOutOfRangeException(),
        };
        bool requiresLicense = kind != VitaPackageKind.Update && info.Metadata.DrmType != (uint)PkgDrmType.Free;

        return new VitaPackageDetails(
            kind,
            contentId,
            titleId,
            info.Sfo?.Title ?? titleId,
            category,
            info.Sfo?.AppVersion,
            info.Sfo?.GetString("PSP2_DISP_VER"),
            info.Header.PspKeyType,
            info.Metadata.DrmType,
            relativeRoot,
            requiresLicense);
    }

    public static VitaExportResult Export(Stream package, string outputDirectory, IKeyProvider keys,
        byte[]? workBin = null, CancellationToken cancellationToken = default,
        IProgress<VitaExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(keys);
        if (!package.CanRead || !package.CanSeek)
            throw new ArgumentException("Package input must be readable and seekable.", nameof(package));

        progress?.Report(new("Reading Vita package", 0));
        PkgInfo info = PkgReader.Read(package, keys);
        VitaPackageDetails details = Inspect(info);
        ValidateWorkBin(workBin, details);

        string baseRoot = Path.GetFullPath(outputDirectory);
        string contentRoot = SafeCombine(baseRoot, details.RelativeRoot);
        Directory.CreateDirectory(contentRoot);

        PkgEntry? bodySource = info.Entries.FirstOrDefault(entry => entry.IsFile &&
            (entry.Name.Equals("sce_sys/package/digs.bin", StringComparison.OrdinalIgnoreCase) ||
             entry.Name.Equals("sce_sys/package/cert.bin", StringComparison.OrdinalIgnoreCase)));
        long totalBytes = info.Entries.Where(entry => entry.IsFile && !ReferenceEquals(entry, bodySource))
            .Sum(entry => checked((long)entry.FileSize));
        var packageProgress = progress is null ? null : new InlineProgress<PkgOperationProgress>(value =>
            progress.Report(new("Extracting Vita content", value.Percent * 0.85)));
        int fileCount = PkgReader.ExtractAll(package, info, contentRoot, keys,
            filter: entry => !ReferenceEquals(entry, bodySource),
            cancellationToken: cancellationToken, progress: packageProgress);
        long completedBytes = totalBytes;

        string packageDirectory = Path.Combine(contentRoot, "sce_sys", "package");
        Directory.CreateDirectory(packageDirectory);
        if (bodySource is not null)
        {
            progress?.Report(new("Preserving Vita package body", 87));
            WriteAtomically(Path.Combine(packageDirectory, "body.bin"), destination =>
                CopyRange(package, checked((long)info.Header.DataOffset + (long)bodySource.FileOffset),
                    checked((long)bodySource.FileSize), destination, cancellationToken));
            completedBytes += checked((long)bodySource.FileSize);
            fileCount++;
        }

        progress?.Report(new("Writing Vita package metadata", 91));
        long itemSize = ResolveItemRegionSize(info);
        long headSize = checked((long)info.Header.DataOffset + itemSize);
        WriteAtomically(Path.Combine(packageDirectory, "head.bin"), destination =>
            CopyRange(package, 0, headSize, destination, cancellationToken));

        long tailOffset = checked((long)info.Header.DataOffset + (long)info.Header.DataSize);
        WriteAtomically(Path.Combine(packageDirectory, "tail.bin"), destination =>
            CopyRange(package, tailOffset, package.Length - tailOffset, destination, cancellationToken));
        WriteAtomically(Path.Combine(packageDirectory, "stat.bin"), destination => destination.Write(new byte[768]));

        VitaLicenseStatus licenseStatus = details.RequiresLicense ? VitaLicenseStatus.Missing : VitaLicenseStatus.NotRequired;
        if (workBin is not null)
        {
            WriteAtomically(Path.Combine(packageDirectory, "work.bin"), destination => destination.Write(workBin));
            licenseStatus = VitaLicenseStatus.Included;
            fileCount++;
        }

        var warnings = new List<string>();
        if (licenseStatus == VitaLicenseStatus.Missing)
            warnings.Add("Package files were exported, but licensed Vita content still requires the matching zRIF/work.bin supplied by the user.");
        warnings.Add("Package AES removal does not decrypt inner Vita PFS/SELF content; retain legitimate license material for use on compatible Vita tooling.");
        progress?.Report(new("Complete", 100));
        return new(details, contentRoot, fileCount + 3, completedBytes, licenseStatus, warnings);
    }

    private static string DlcDirectory(ContentId parsed, string contentId)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Name)) return parsed.Name;
        if (contentId.Length > 20) return contentId[20..];
        throw new PkgFormatException("Vita DLC content ID does not contain a DLC directory name.");
    }

    private static void ValidateWorkBin(byte[]? workBin, VitaPackageDetails details)
    {
        if (workBin is null) return;
        if (workBin.Length != WorkBinSize)
            throw new PkgFormatException($"Vita work.bin/RIF material must be exactly {WorkBinSize} bytes.");
        string licenseContentId = Encoding.ASCII.GetString(workBin, 0x10, 0x30).TrimEnd('\0');
        if (!licenseContentId.Equals(details.ContentId, StringComparison.Ordinal))
            throw new PkgKeyException($"License content ID '{licenseContentId}' does not match package '{details.ContentId}'.");
    }

    private static long ResolveItemRegionSize(PkgInfo info)
    {
        if (info.Metadata.PsVitaItemRange is { } range && range.Size > 0)
            return range.Size;
        long namesEnd = info.Entries.Count == 0
            ? 0
            : info.Entries.Max(entry => checked((long)entry.NameOffset + entry.NameSize));
        return Math.Max((long)info.Header.ItemCount * PkgEntry.RecordSize, namesEnd);
    }

    private static string SafeCombine(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root);
        string rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot,
            relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && full != fullRoot)
            throw new PkgFormatException($"Vita export path '{relative}' escapes the output directory.");
        return full;
    }

    private static void CopyRange(Stream source, long offset, long length, Stream destination,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 0 || offset > source.Length || length > source.Length - offset)
            throw new PkgFormatException("Vita package metadata references a range outside the PKG.");
        source.Position = offset;
        var buffer = new byte[1024 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, wanted);
            if (read <= 0) throw new EndOfStreamException("Vita package ended while copying package metadata.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void WriteAtomically(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                write(destination);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
