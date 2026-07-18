using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Ps1;

public enum Ps1ClassicSourceKind
{
    EbootPbp,
    NpdrmEdat,
}

public enum Ps1ClassicImageKind
{
    SingleDisc,
    MultiDisc,
}

public sealed record Ps1ClassicExportEligibility(
    bool CanExport,
    string Reason,
    Ps1ClassicSourceKind? SourceKind = null,
    string? PackageEntry = null,
    string? LicenseContentId = null,
    bool RapRequired = false,
    bool RapAvailable = false,
    Ps1ClassicImageKind? ImageKind = null);

public sealed record Ps1ClassicExportProgress(string Stage, double Percentage);

public sealed record Ps1ClassicPreparedExport(
    string ContentId,
    string PackageEntry,
    Ps1ClassicSourceKind SourceKind,
    Ps1ClassicImageKind ImageKind,
    string EbootPath,
    string MetadataDirectory,
    string ManifestPath,
    string? DocumentPath,
    string LicenseSource);

public sealed record Ps1ClassicReconstructionResult(
    IReadOnlyList<string> BinFiles,
    IReadOnlyList<string> CueFiles,
    string LogPath);

public static class Ps1ClassicPackageExporter
{
    private static readonly byte[] SingleDiscMagic = Encoding.ASCII.GetBytes("PSISOIMG0000");
    private static readonly byte[] MultiDiscMagic = Encoding.ASCII.GetBytes("PSTITLEIMG000000");

    public static Ps1ClassicExportEligibility CheckEligibility(Stream package, IKeyProvider keys,
        string? rapDirectory = null, string? rapOverridePath = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(keys);
        PkgInfo info = PkgReader.Read(package, keys);
        return CheckEligibility(package, info, keys, rapDirectory, rapOverridePath);
    }

    public static Ps1ClassicExportEligibility CheckEligibility(Stream package, PkgInfo info, IKeyProvider keys,
        string? rapDirectory = null, string? rapOverridePath = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(keys);

        if (!info.Header.IsPs3 || info.Metadata.ContentType != PkgContentType.Ps1Emu)
            return new(false,
                $"This is a {info.Header.PlatformDisplay} {info.Metadata.ContentType?.ToString() ?? "package"}, not a PS1 Classic package.");
        if (!info.IsDecrypted)
            return new(false, info.DecryptionNote ?? "The package contents could not be decrypted.");

        PkgEntry? eboot = FindEntry(info, "EBOOT.PBP");
        if (eboot is not null)
        {
            try
            {
                using Stream input = PkgReader.OpenEntry(package, info.Header, eboot, keys);
                Ps1ClassicImageKind imageKind = InspectPbp(input);
                return new(true,
                    $"PS1 Classic {ImageKindText(imageKind)} image detected in EBOOT.PBP; no RAP is needed for package extraction.",
                    Ps1ClassicSourceKind.EbootPbp, eboot.Name, ImageKind: imageKind, RapAvailable: true);
            }
            catch (PkgFormatException ex)
            {
                return new(false, ex.Message, Ps1ClassicSourceKind.EbootPbp, eboot.Name);
            }
        }

        PkgEntry? edat = FindEntry(info, "ISO.BIN.EDAT");
        if (edat is null)
            return new(false, "This PS1 Classic package contains neither EBOOT.PBP nor ISO.BIN.EDAT.");

        try
        {
            using Stream input = PkgReader.OpenEntry(package, info.Header, edat, keys);
            NpdInfo npd = EdatFile.ParseHeader(input);
            (byte[]? klicensee, _) = ResolveKlicensee(npd, rapDirectory, rapOverridePath);
            bool ready = !npd.NeedsKlicensee || klicensee is not null;
            return new(true,
                ready
                    ? $"PS1 Classic NPDRM image detected; license material is ready for {npd.ContentId}."
                    : $"PS1 Classic NPDRM image detected, but a matching RAP is required for {npd.ContentId}.",
                Ps1ClassicSourceKind.NpdrmEdat, edat.Name, npd.ContentId,
                RapRequired: npd.NeedsKlicensee, RapAvailable: ready);
        }
        catch (Exception ex) when (ex is PkgFormatException or PkgKeyException)
        {
            return new(false, ex.Message, Ps1ClassicSourceKind.NpdrmEdat, edat.Name);
        }
    }

    public static Ps1ClassicPreparedExport Prepare(Stream package, string outputDirectory, IKeyProvider keys,
        string? rapDirectory = null, string? rapOverridePath = null,
        CancellationToken cancellationToken = default, IProgress<Ps1ClassicExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new("Reading package", 0));
        PkgInfo info = PkgReader.Read(package, keys);
        Ps1ClassicExportEligibility eligibility = CheckEligibility(package, info, keys, rapDirectory, rapOverridePath);
        if (!eligibility.CanExport)
            throw new PkgFormatException(eligibility.Reason);
        if (!eligibility.RapAvailable)
            throw new PkgKeyException(eligibility.Reason);

        Directory.CreateDirectory(outputDirectory);
        string metadataDirectory = Path.Combine(outputDirectory, "metadata");
        Directory.CreateDirectory(metadataDirectory);
        string ebootPath = Path.Combine(outputDirectory, "EBOOT.PBP");
        string licenseSource;

        PkgEntry sourceEntry = info.Entries.Single(entry => entry.Name == eligibility.PackageEntry);
        if (eligibility.SourceKind == Ps1ClassicSourceKind.EbootPbp)
        {
            progress?.Report(new("Extracting EBOOT.PBP", 8));
            using var destination = new FileStream(ebootPath, FileMode.Create, FileAccess.Write, FileShare.None);
            PkgReader.ExtractEntry(package, info.Header, sourceEntry, destination, keys, cancellationToken,
                new Progress<long>(bytes => progress?.Report(new("Extracting EBOOT.PBP",
                    8 + bytes * 42d / Math.Max(1d, sourceEntry.FileSize)))));
            licenseSource = "package EBOOT.PBP";
        }
        else
        {
            progress?.Report(new("Decrypting ISO.BIN.EDAT", 8));
            string psarPath = Path.Combine(outputDirectory, $".pkglens-ps1-{Guid.NewGuid():N}.psar");
            try
            {
                using (Stream edat = PkgReader.OpenEntry(package, info.Header, sourceEntry, keys))
                {
                    NpdInfo npd = EdatFile.ParseHeader(edat);
                    (byte[]? klicensee, string source) = ResolveKlicensee(npd, rapDirectory, rapOverridePath);
                    licenseSource = source;
                    edat.Position = 0;
                    using var psar = new FileStream(psarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    EdatFile.Decrypt(edat, psar, klicensee);
                }

                cancellationToken.ThrowIfCancellationRequested();
                using var plaintext = File.OpenRead(psarPath);
                Span<byte> magic = stackalloc byte[16];
                plaintext.ReadExactly(magic);
                plaintext.Position = 0;
                if (PbpArchive.IsPbp(magic))
                    plaintext.CopyToWithCancellation(ebootPath, cancellationToken);
                else
                    WritePbpWithPsar(plaintext, ebootPath, cancellationToken);
            }
            finally
            {
                try { File.Delete(psarPath); } catch { }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("Exporting PS1 metadata", 55));
        Ps1ClassicImageKind imageKind;
        var sections = new List<object>();
        using (var eboot = File.OpenRead(ebootPath))
        {
            PbpArchive archive = PbpArchive.Parse(eboot);
            imageKind = InspectPbp(eboot);
            foreach (PbpEntry entry in archive.Entries.Where(entry => entry.Name != "DATA.PSAR" && entry.Size > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(metadataDirectory, entry.Name);
                using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                PbpArchive.Extract(eboot, entry, output);
                sections.Add(new { entry.Name, entry.Size, File = Path.GetRelativePath(outputDirectory, path) });
            }
        }

        string? documentPath = ExtractOptionalEntry(package, info, keys, outputDirectory, "DOCUMENT.DAT", cancellationToken);
        string manifestPath = Path.Combine(outputDirectory, "ps1-classic.json");
        var manifest = new
        {
            Format = "PkgLens PS1 Classics export",
            SourceContentId = info.ContentId.Raw,
            Title = info.Sfo?.Title,
            TitleId = info.Sfo?.TitleId,
            PackageEntry = sourceEntry.Name,
            SourceKind = eligibility.SourceKind!.Value.ToString(),
            ImageKind = imageKind.ToString(),
            LicenseContentId = eligibility.LicenseContentId,
            LicenseSource = licenseSource,
            Eboot = Path.GetFileName(ebootPath),
            Document = documentPath is null ? null : Path.GetFileName(documentPath),
            Sections = sections,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        progress?.Report(new("Prepared for BIN/CUE reconstruction", 100));

        return new(info.ContentId.Raw, sourceEntry.Name, eligibility.SourceKind!.Value, imageKind,
            ebootPath, metadataDirectory, manifestPath, documentPath, licenseSource);
    }

    public static async Task<Ps1ClassicReconstructionResult> ReconstructAsync(
        Ps1ClassicPreparedExport prepared, string psxtractPath, string outputDirectory,
        CancellationToken cancellationToken = default, IProgress<Ps1ClassicExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentException.ThrowIfNullOrWhiteSpace(psxtractPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!File.Exists(psxtractPath))
            throw new FileNotFoundException("The selected psxtract executable does not exist.", psxtractPath);
        if (!File.Exists(prepared.EbootPath))
            throw new FileNotFoundException("The prepared PS1 EBOOT.PBP does not exist.", prepared.EbootPath);

        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException("The BIN/CUE output directory must be empty so psxtract cannot prompt for overwrites.");

        var start = new ProcessStartInfo
        {
            FileName = psxtractPath,
            WorkingDirectory = outputDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(prepared.EbootPath);
        if (prepared.DocumentPath is not null)
            start.ArgumentList.Add(prepared.DocumentPath);

        progress?.Report(new("Reconstructing PS1 BIN/CUE", 5));
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("psxtract could not be started.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        });
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        string logPath = Path.Combine(outputDirectory, "psxtract.log");
        await File.WriteAllTextAsync(logPath,
            output + (string.IsNullOrWhiteSpace(error) ? string.Empty : Environment.NewLine + error), cancellationToken)
            .ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new PkgFormatException($"psxtract exited with code {process.ExitCode}. See {logPath} for details.");

        string[] bins = Directory.GetFiles(outputDirectory, "*.bin", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals("STARTDAT.BIN", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] cues = Directory.GetFiles(outputDirectory, "*.cue", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (bins.Length == 0 || cues.Length == 0)
            throw new PkgFormatException($"psxtract completed but did not produce a verified BIN/CUE pair. See {logPath}.");
        foreach (string bin in bins)
        {
            if (new FileInfo(bin).Length == 0 || new FileInfo(bin).Length % 2352 != 0)
                throw new PkgFormatException($"Reconstructed image '{Path.GetFileName(bin)}' is empty or not aligned to 2352-byte PS1 sectors.");
        }

        progress?.Report(new("BIN/CUE reconstruction complete", 100));
        return new(bins, cues, logPath);
    }

    public static string BuildReport(Ps1ClassicPreparedExport prepared,
        Ps1ClassicReconstructionResult? reconstruction, string sourcePackage)
    {
        var lines = new List<string>
        {
            "PS1 CLASSICS EXPORT",
            $"Source package: {sourcePackage}",
            $"Package content ID: {prepared.ContentId}",
            $"Package entry: {prepared.PackageEntry}",
            $"Source format: {prepared.SourceKind}",
            $"Disc set: {ImageKindText(prepared.ImageKind)}",
            $"License source: {prepared.LicenseSource}",
            $"EBOOT.PBP: {prepared.EbootPath}",
            $"Metadata manifest: {prepared.ManifestPath}",
            prepared.DocumentPath is null ? "Software manual: not present" : $"Software manual: {prepared.DocumentPath}",
        };
        if (reconstruction is null)
        {
            lines.Add("BIN/CUE reconstruction: not requested");
        }
        else
        {
            lines.Add($"BIN outputs: {string.Join(", ", reconstruction.BinFiles.Select(Path.GetFileName))}");
            lines.Add($"CUE outputs: {string.Join(", ", reconstruction.CueFiles.Select(Path.GetFileName))}");
            lines.Add($"Reconstruction log: {reconstruction.LogPath}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static Ps1ClassicImageKind InspectPbp(Stream eboot)
    {
        PbpArchive archive = PbpArchive.Parse(eboot);
        PbpEntry psar = archive.Entries.SingleOrDefault(entry => entry.Name == "DATA.PSAR")
            ?? throw new PkgFormatException("EBOOT.PBP does not contain DATA.PSAR.");
        if (psar.Size < 16)
            throw new PkgFormatException("PS1 DATA.PSAR is truncated.");
        var magic = new byte[16];
        eboot.Position = psar.Offset;
        eboot.ReadExactly(magic);
        if (magic.AsSpan(0, SingleDiscMagic.Length).SequenceEqual(SingleDiscMagic))
            return Ps1ClassicImageKind.SingleDisc;
        if (magic.AsSpan().SequenceEqual(MultiDiscMagic))
            return Ps1ClassicImageKind.MultiDisc;
        throw new PkgFormatException("EBOOT.PBP DATA.PSAR is not a PS1 Classic PSISOIMG/PSTITLEIMG image.");
    }

    private static void WritePbpWithPsar(Stream psar, string outputPath, CancellationToken cancellationToken)
    {
        Span<byte> magic = stackalloc byte[16];
        psar.ReadExactly(magic);
        psar.Position = 0;
        if (!magic[..SingleDiscMagic.Length].SequenceEqual(SingleDiscMagic) && !magic.SequenceEqual(MultiDiscMagic))
            throw new PkgFormatException("Decrypted ISO.BIN.EDAT is neither a PBP nor a PS1 Classic PSISOIMG/PSTITLEIMG image.");

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[PbpArchive.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, PbpArchive.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0x00010000);
        for (int index = 0; index < 8; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(header[(8 + index * 4)..], PbpArchive.HeaderSize);
        output.Write(header);
        CopyTo(psar, output, cancellationToken);
    }

    private static string? ExtractOptionalEntry(Stream package, PkgInfo info, IKeyProvider keys,
        string outputDirectory, string fileName, CancellationToken cancellationToken)
    {
        PkgEntry? entry = FindEntry(info, fileName);
        if (entry is null) return null;
        string path = Path.Combine(outputDirectory, fileName);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        PkgReader.ExtractEntry(package, info.Header, entry, output, keys, cancellationToken);
        return path;
    }

    private static PkgEntry? FindEntry(PkgInfo info, string fileName) => info.Entries
        .Where(entry => entry.IsFile && Path.GetFileName(entry.Name).Equals(fileName, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(entry => entry.Name.Replace('\\', '/').StartsWith("USRDIR/", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault();

    private static (byte[]? Klicensee, string Source) ResolveKlicensee(NpdInfo npd,
        string? rapDirectory, string? rapOverridePath)
    {
        if (!npd.NeedsKlicensee)
            return (null, npd.IsSdat ? "self-keyed SDAT" : "free NPDRM key");
        if (!string.IsNullOrWhiteSpace(rapOverridePath))
        {
            byte[] rap = File.ReadAllBytes(rapOverridePath);
            if (rap.Length != 16) throw new PkgKeyException("The selected RAP must be exactly 16 bytes.");
            return (NpdKeys.RapToKlicensee(rap), "selected RAP override");
        }
        byte[]? stored = RapStore.Find(npd.ContentId, rapDirectory);
        return stored is null ? (null, "missing RAP") : (NpdKeys.RapToKlicensee(stored), "RAP library");
    }

    private static void CopyTo(this Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
        }
    }

    private static void CopyToWithCancellation(this Stream source, string outputPath, CancellationToken cancellationToken)
    {
        using var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        CopyTo(source, destination, cancellationToken);
    }

    private static string ImageKindText(Ps1ClassicImageKind imageKind) =>
        imageKind == Ps1ClassicImageKind.MultiDisc ? "multi-disc" : "single-disc";
}
