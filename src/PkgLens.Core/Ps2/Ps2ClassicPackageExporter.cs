using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Ps2;

public sealed record Ps2ClassicExportEligibility(
    bool CanExport, string Reason, string? ImageEntry = null, string? ContentId = null,
    long IsoSize = 0, bool RapRequired = false, bool RapAvailable = false);

public sealed record Ps2ClassicExportResult(
    string ContentId, string ImageEntry, long IsoSize, Ps2ClassicMode Mode,
    string LicenseSource, string? CfwPackagePath = null, string? ReportPath = null);

public static class Ps2ClassicPackageExporter
{
    public static Ps2ClassicExportEligibility CheckEligibility(Stream package, IKeyProvider keys,
        string? rapDirectory = null, string? rapOverridePath = null)
    {
        PkgInfo info = PkgReader.Read(package, keys);
        return CheckEligibility(package, info, keys, rapDirectory, rapOverridePath);
    }

    public static Ps2ClassicExportEligibility CheckEligibility(Stream package, PkgInfo info, IKeyProvider keys,
        string? rapDirectory = null, string? rapOverridePath = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.Header.IsPs3 || info.Metadata.ContentType != PkgContentType.Ps2Classic)
            return new(false, $"This is a {info.Header.PlatformDisplay} {info.Metadata.ContentType?.ToString() ?? "package"}, not a PS2 Classic package.");
        if (!info.IsDecrypted)
            return new(false, info.DecryptionNote ?? "The package contents could not be decrypted.");
        PkgEntry? image = FindEntry(info, "ISO.BIN.ENC");
        if (image is null)
            return new(false, "This PS2 Classic package does not contain ISO.BIN.ENC.");
        try
        {
            using Stream entry = PkgReader.OpenEntry(package, info.Header, image, keys);
            Ps2ClassicImageInfo imageInfo = Ps2ClassicImage.ParseHeader(entry);
            (byte[]? klicensee, _) = ResolveKlicensee(imageInfo.ContentId, rapDirectory, rapOverridePath);
            bool builtIn = IsBuiltInContentId(imageInfo.ContentId);
            return new(true,
                klicensee is not null
                    ? $"PS2 Classic disc detected; license material is ready for {imageInfo.ContentId}."
                    : $"PS2 Classic disc detected, but a matching RAP is required for {imageInfo.ContentId}.",
                image.Name, imageInfo.ContentId, imageInfo.IsoSize, !builtIn, klicensee is not null);
        }
        catch (Exception ex) when (ex is PkgFormatException or PkgKeyException)
        {
            return new(false, ex.Message, image.Name);
        }
    }

    public static Ps2ClassicExportResult ExportIso(Stream package, Stream destination, IKeyProvider keys,
        string? rapDirectory = null, string? rapOverridePath = null,
        CancellationToken cancellationToken = default, IProgress<Ps2ClassicProgress>? progress = null)
    {
        PkgInfo info = PkgReader.Read(package, keys);
        Ps2ClassicExportEligibility eligibility = CheckEligibility(package, info, keys, rapDirectory, rapOverridePath);
        if (!eligibility.CanExport) throw new PkgFormatException(eligibility.Reason);
        if (!eligibility.RapAvailable) throw new PkgKeyException(eligibility.Reason);
        PkgEntry image = info.Entries.Single(entry => entry.Name == eligibility.ImageEntry);
        (byte[]? klicensee, string source) = ResolveKlicensee(eligibility.ContentId!, rapDirectory, rapOverridePath);
        using Stream encrypted = PkgReader.OpenEntry(package, info.Header, image, keys);
        Ps2ClassicImageInfo result = Ps2ClassicImage.Decrypt(encrypted, destination, klicensee!, cancellationToken, progress);
        return new(result.ContentId, image.Name, result.IsoSize, result.Mode!.Value, source);
    }

    public static Ps2ClassicExportResult RebuildCfwPackage(Stream package, Stream destination, IKeyProvider keys,
        string isoPath, string outputPackagePath, string? rapDirectory = null, string? rapOverridePath = null,
        string? temporaryDirectory = null, CancellationToken cancellationToken = default,
        IProgress<Ps2ClassicProgress>? progress = null)
    {
        PkgInfo info = PkgReader.Read(package, keys);
        Ps2ClassicExportEligibility eligibility = CheckEligibility(package, info, keys, rapDirectory, rapOverridePath);
        if (!eligibility.CanExport) throw new PkgFormatException(eligibility.Reason);
        if (!eligibility.RapAvailable) throw new PkgKeyException(eligibility.Reason);
        PkgEntry image = info.Entries.Single(entry => entry.Name == eligibility.ImageEntry);
        PkgEntry? edatEntry = FindEntry(info, "ISO.BIN.EDAT");
        if (edatEntry is null)
            throw new PkgFormatException("A self-contained CFW rebuild requires ISO.BIN.EDAT, but the package does not contain it.");

        string tempRoot = temporaryDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(tempRoot);
        string encryptedPath = Path.Combine(tempRoot, $".pkglens-ps2-{Guid.NewGuid():N}.ISO.BIN.ENC");
        try
        {
            using (var iso = File.OpenRead(isoPath))
            using (var encrypted = new FileStream(encryptedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Ps2ClassicImage.Encrypt(iso, encrypted, Ps2ClassicImage.PlaceholderKlicensee,
                    cancellationToken: cancellationToken, progress: progress);

            byte[] edatPlaintext;
            using (Stream edat = PkgReader.OpenEntry(package, info.Header, edatEntry, keys))
            {
                NpdInfo npd = EdatFile.ParseHeader(edat);
                (byte[]? edatKlicensee, _) = ResolveKlicensee(npd.ContentId, rapDirectory, rapOverridePath);
                if (npd.NeedsKlicensee && edatKlicensee is null)
                    throw new PkgKeyException($"A matching RAP is required to convert {edatEntry.Name} ({npd.ContentId}).");
                edatPlaintext = EdatFile.DecryptToArray(edat, edatKlicensee);
            }
            byte[] placeholderEdat = EdatBuilder.BuildLicensed(edatPlaintext,
                Ps2ClassicImage.PlaceholderContentId, "ISO.BIN.EDAT", Ps2ClassicImage.PlaceholderKlicensee);
            var replacements = new Dictionary<PkgEntry, PkgReplacement>
            {
                [image] = PkgReplacement.FromFile(encryptedPath),
                [edatEntry] = PkgReplacement.FromBytes(placeholderEdat),
            };
            var packageProgress = progress is null ? null : new Progress<PkgOperationProgress>(value =>
                progress.Report(new Ps2ClassicProgress($"Rebuilding package: {value.Item}", value.Completed, value.Total)));
            package.Position = 0;
            PkgWriter.Repack(package, info, replacements, keys, destination, cancellationToken, packageProgress);
            return new(Ps2ClassicImage.PlaceholderContentId, image.Name, new FileInfo(isoPath).Length,
                Ps2ClassicMode.Cex, "CFW placeholder klicensee", outputPackagePath);
        }
        finally
        {
            try { File.Delete(encryptedPath); } catch { }
        }
    }

    public static string BuildReport(Ps2ClassicExportResult result, string sourcePackage, string isoPath)
    {
        return string.Join(Environment.NewLine,
            "PS2 CLASSICS EXPORT", $"Source package: {sourcePackage}", $"Image entry: {result.ImageEntry}",
            $"Original content ID: {result.ContentId}", $"License source: {result.LicenseSource}",
            $"Disc mode: {result.Mode}", $"ISO output: {isoPath}", $"ISO size: {result.IsoSize}",
            result.CfwPackagePath is null ? "CFW package: not requested" : $"CFW package: {result.CfwPackagePath}",
            result.CfwPackagePath is null ? string.Empty :
                $"CFW transformation: ISO.BIN.ENC and ISO.BIN.EDAT converted to {Ps2ClassicImage.PlaceholderContentId}; package rebuilt for CFW/HEN.");
    }

    private static PkgEntry? FindEntry(PkgInfo info, string fileName) => info.Entries
        .Where(entry => entry.IsFile && Path.GetFileName(entry.Name).Equals(fileName, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(entry => entry.Name.Replace('\\', '/').StartsWith("USRDIR/", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault();

    private static (byte[]? Klicensee, string Source) ResolveKlicensee(string contentId,
        string? rapDirectory, string? rapOverridePath)
    {
        if (contentId.Equals(Ps2ClassicImage.PlaceholderContentId, StringComparison.OrdinalIgnoreCase))
            return (Ps2ClassicImage.PlaceholderKlicensee.ToArray(), "built-in PS2 Classics placeholder key");
        if (contentId.Equals(Ps2ClassicImage.ReactPsnContentId, StringComparison.OrdinalIgnoreCase))
            return (Ps2ClassicImage.ReactPsnKlicensee.ToArray(), "built-in reactPSN key");
        if (!string.IsNullOrWhiteSpace(rapOverridePath))
        {
            byte[] rap = File.ReadAllBytes(rapOverridePath);
            if (rap.Length != 16) throw new PkgKeyException("The selected RAP must be exactly 16 bytes.");
            return (NpdKeys.RapToKlicensee(rap), "selected RAP override");
        }
        byte[]? stored = RapStore.Find(contentId, rapDirectory);
        return stored is null ? (null, "missing RAP") : (NpdKeys.RapToKlicensee(stored), "RAP library");
    }

    private static bool IsBuiltInContentId(string contentId) =>
        contentId.Equals(Ps2ClassicImage.PlaceholderContentId, StringComparison.OrdinalIgnoreCase) ||
        contentId.Equals(Ps2ClassicImage.ReactPsnContentId, StringComparison.OrdinalIgnoreCase);
}
