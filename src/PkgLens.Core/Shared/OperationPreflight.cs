using System.Buffers.Binary;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Ps2;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Vita;

namespace PkgLens.Core.Shared;

public enum PreflightCheckStatus
{
    Pass,
    Warning,
    Error,
}

public sealed record PreflightCheck(string Name, PreflightCheckStatus Status, string Details);

public sealed class OperationPreflightReport
{
    public required string Operation { get; init; }
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public long? ExpectedOutputBytes { get; init; }
    public long? AvailableBytes { get; init; }
    public IReadOnlyList<PreflightCheck> Checks { get; init; } = Array.Empty<PreflightCheck>();

    public int ErrorCount => Checks.Count(check => check.Status == PreflightCheckStatus.Error);
    public int WarningCount => Checks.Count(check => check.Status == PreflightCheckStatus.Warning);
    public bool CanProceed => ErrorCount == 0;
}

/// <summary>Read-only readiness checks shared by GUI decrypt, export, and conversion workflows.</summary>
public static class OperationPreflight
{
    public static OperationPreflightReport Ps2ClassicExport(string inputPath, string outputPath,
        IKeyProvider keys, string? rapDirectory, bool rebuildCfwPackage)
    {
        using var package = File.OpenRead(inputPath);
        PkgInfo info = PkgReader.Read(package, keys);
        Ps2ClassicExportEligibility eligibility = Ps2ClassicPackageExporter.CheckEligibility(
            package, info, keys, rapDirectory);
        var checks = new List<PreflightCheck>
        {
            Check("Package type", eligibility.CanExport, eligibility.Reason),
            Check("Package key", info.IsDecrypted,
                info.IsDecrypted ? "PS3 package contents decrypted successfully."
                    : info.DecryptionNote ?? "The package key could not be resolved."),
            Check("PS2 disc image", eligibility.CanExport,
                eligibility.CanExport ? $"Authenticated ISO.BIN.ENC header detected; expected ISO size {FormatBytes(eligibility.IsoSize)}."
                    : eligibility.Reason),
            Check("License / RAP", eligibility.RapAvailable,
                eligibility.RapAvailable
                    ? eligibility.RapRequired ? $"A RAP for {eligibility.ContentId} is available."
                        : $"The built-in key for {eligibility.ContentId} is available."
                    : $"A valid RAP for {eligibility.ContentId} is required."),
        };
        if (rebuildCfwPackage)
        {
            PkgEntry? edatEntry = info.Entries.FirstOrDefault(entry => entry.IsFile &&
                Path.GetFileName(entry.Name).Equals("ISO.BIN.EDAT", StringComparison.OrdinalIgnoreCase));
            checks.Add(Check("CFW package license record", edatEntry is not null,
                edatEntry is not null ? "ISO.BIN.EDAT is present and will be converted to the standard PS2 Classics placeholder license."
                    : "ISO.BIN.EDAT is missing; a self-contained CFW package cannot be created."));
            if (edatEntry is not null)
            {
                try
                {
                    using Stream edat = PkgReader.OpenEntry(package, info.Header, edatEntry, keys);
                    NpdInfo npd = EdatFile.ParseHeader(edat);
                    bool builtIn = npd.ContentId.Equals(Ps2ClassicImage.PlaceholderContentId, StringComparison.OrdinalIgnoreCase) ||
                                   npd.ContentId.Equals(Ps2ClassicImage.ReactPsnContentId, StringComparison.OrdinalIgnoreCase);
                    bool available = !npd.NeedsKlicensee || builtIn || RapStore.Find(npd.ContentId, rapDirectory) is not null;
                    checks.Add(Check("EDAT license / RAP", available,
                        available ? $"License material for {npd.ContentId} is available."
                            : $"A valid RAP for the ISO.BIN.EDAT content ID {npd.ContentId} is required."));
                }
                catch (Exception ex) when (ex is PkgFormatException or PkgKeyException or ArgumentException)
                {
                    checks.Add(Error("EDAT license / RAP", ex.Message));
                }
            }
            checks.Add(Warning("CFW/HEN only", "The rebuilt package is unsigned and will not install on stock firmware."));
        }
        long expected = eligibility.IsoSize;
        if (rebuildCfwPackage)
            expected = checked(expected + new FileInfo(inputPath).Length);
        checks.Add(Pass("Expected output", rebuildCfwPackage
            ? $"Approximately {FormatBytes(expected)} across the ISO and rebuilt package."
            : $"Exact ISO size: {FormatBytes(expected)}."));
        return Complete(rebuildCfwPackage ? "Export PS2 Classic and rebuild CFW package" : "Export PS2 Classic ISO",
            inputPath, outputPath, expected, outputIsDirectory: false, checks);
    }

    public static OperationPreflightReport PspExport(string inputPath, string outputPath,
        PspExportFormat format, IKeyProvider keys)
    {
        using var package = File.OpenRead(inputPath);
        PkgInfo info = PkgReader.Read(package, keys);
        PspExportEligibility eligibility = PspPackageExporter.CheckEligibility(info);
        var checks = new List<PreflightCheck>
        {
            Check("Package type", eligibility.CanExport, eligibility.Reason),
            Check("Package key", info.IsDecrypted,
                info.IsDecrypted ? $"{info.Header.PlatformDisplay} package contents decrypted successfully."
                    : info.DecryptionNote ?? "The package key could not be resolved."),
            Check("Encryption support", eligibility.CanExport,
                eligibility.CanExport ? "PSP package and EBOOT.PBP encryption are supported."
                    : eligibility.Reason),
            Pass("License / RAP", "PSP package export does not use a PS3 RAP."),
        };
        PkgEntry? eboot = eligibility.PackageEntry is { } entryName
            ? info.Entries.Single(entry => entry.Name == entryName)
            : null;
        long expected = eboot is null ? new FileInfo(inputPath).Length : checked((long)eboot.FileSize);
        string estimate;
        if (format == PspExportFormat.Pbp || eboot is null)
        {
            estimate = $"Exact EBOOT.PBP size: {FormatBytes(expected)}.";
            checks.Add(Pass("Expected output", estimate));
        }
        else
        {
            byte[] pbpHeader = PkgReader.ExtractEntryRange(package, info.Header, eboot, keys, 0, PbpArchive.HeaderSize);
            if (!PbpArchive.IsPbp(pbpHeader))
            {
                checks.Add(Error("Disc image", "EBOOT.PBP has invalid PBP magic."));
            }
            else
            {
                long psarOffset = BinaryPrimitives.ReadUInt32LittleEndian(pbpHeader.AsSpan(0x24));
                byte[] npHeader = PkgReader.ExtractEntryRange(package, info.Header, eboot, keys, psarOffset, 0x100);
                NpumdImgInfo image = NpumdImg.ParseHeader(npHeader);
                checks.Add(Check("Disc encryption", image.HeaderValid,
                    image.HeaderValid ? $"NPUMDIMG header decrypted successfully; disc {image.DiscId}."
                        : "NPUMDIMG header did not decrypt to a supported layout."));
                expected = checked(image.TotalSectors * image.SectorSize);
                estimate = format == PspExportFormat.Iso
                    ? $"Exact ISO size: {FormatBytes(expected)}."
                    : $"CSO upper bound: {FormatBytes(expected)} before compression.";
                checks.Add(Pass("Expected output", estimate));
            }
        }
        return Complete($"Export PSP {format.ToString().ToUpperInvariant()}", inputPath, outputPath,
            expected, outputIsDirectory: false, checks);
    }

    public static OperationPreflightReport VitaExport(string inputPath, string destinationRoot,
        IKeyProvider keys, bool workBinProvided)
    {
        using var package = File.OpenRead(inputPath);
        PkgInfo info = PkgReader.Read(package, keys);
        var checks = new List<PreflightCheck>();
        VitaPackageDetails? details = null;
        try
        {
            details = VitaPackageExporter.Inspect(info);
            checks.Add(Pass("Package type", $"PSVita {details.Kind} package; output layout {details.RelativeRoot}."));
        }
        catch (Exception ex) when (ex is PkgFormatException or PkgKeyException)
        {
            checks.Add(Error("Package type", ex.Message));
        }
        checks.Add(Check("Package key", info.IsDecrypted,
            info.IsDecrypted ? $"Vita package key revision {info.Header.PspKeyType} resolved."
                : info.DecryptionNote ?? "The Vita package key could not be resolved."));
        checks.Add(Check("Encryption support", info.Header.PspKeyType is >= 2 and <= 4,
            info.Header.PspKeyType is >= 2 and <= 4
                ? $"Vita package encryption revision {info.Header.PspKeyType} is supported."
                : $"Vita package encryption revision {info.Header.PspKeyType} is unsupported."));

        if (details is { RequiresLicense: true })
            checks.Add(workBinProvided
                ? Pass("License material", "A work.bin/RIF file was selected for the licensed Vita content.")
                : Warning("License material", "Export can continue, but inner Vita content still needs matching work.bin/zRIF material."));
        else
            checks.Add(Pass("License material", "No additional Vita license material is required for this package type."));

        long expected = SumEntryBytes(info);
        checks.Add(Pass("Expected output", $"Approximately {FormatBytes(expected)} across {info.FileCount} package files."));
        string output = details is null ? destinationRoot : Path.Combine(destinationRoot,
            details.RelativeRoot.Replace('/', Path.DirectorySeparatorChar));
        return Complete("Export PSVita package", inputPath, output, expected,
            outputIsDirectory: true, checks);
    }

    public static OperationPreflightReport CfwConversion(string inputPath, string outputPath,
        IKeyProvider keys, string? rapDirectory,
        TargetCompatibilityProfile profile = TargetCompatibilityProfile.CexCfw,
        CfwFirmwareTarget? firmwareTarget = null)
    {
        TargetCompatibilityReport compatibility = TargetCompatibilityAnalyzer.AnalyzeCfwConversion(
            inputPath, keys, rapDirectory, profile, firmwareTarget);
        var checks = compatibility.Checks.Select(check => new PreflightCheck(
            check.Name,
            check.Status switch
            {
                TargetCompatibilityStatus.Pass => PreflightCheckStatus.Pass,
                TargetCompatibilityStatus.Warning => PreflightCheckStatus.Warning,
                _ => PreflightCheckStatus.Error,
            },
            check.Details)).ToList();

        long expected = new FileInfo(inputPath).Length;
        checks.Add(Warning("Expected output", $"Estimated rebuilt package size: {FormatBytes(expected)}; replacements may change it slightly."));
        return Complete($"Convert package for {compatibility.Target.Name}", inputPath, outputPath, expected,
            outputIsDirectory: false, checks);
    }

    public static OperationPreflightReport DataDecrypt(string inputPath, string outputPath,
        string? rapOverridePath, string? rapDirectory)
    {
        using var input = File.OpenRead(inputPath);
        var checks = new List<PreflightCheck>();
        long expected;
        if (PspEdatFile.IsPspEncrypted(input))
        {
            byte[] prefix = ReadPrefix(input, 0x40);
            if (PspEdatFile.IsPspEdat(prefix))
            {
                PspEdatInfo info = PspEdatFile.ParseHeader(prefix);
                checks.Add(Pass("File type", $"PSP EDAT detected ({info.ContentId})."));
                checks.Add(Check("Encryption support", info.DrmFreeOrLocal,
                    info.DrmFreeOrLocal ? "Fixed-key PSP EDAT encryption is supported."
                        : "This PSP EDAT is fuse-bound and cannot be decrypted by PkgLens."));
            }
            else
            {
                checks.Add(Pass("File type", "Bare PSP PGD detected."));
                checks.Add(Warning("Encryption support", "Fixed-key and fuse-bound PGD use the same magic; support is confirmed during decryption."));
            }
            checks.Add(Pass("License / RAP", "PSP EDAT/PGD decryption does not use a PS3 RAP."));
            expected = input.Length;
        }
        else
        {
            NpdInfo info = EdatFile.ParseHeader(input);
            checks.Add(Pass("File type", $"{(info.IsSdat ? "SDAT" : "EDAT")} v{info.Version} detected ({info.ContentId})."));
            checks.Add(Check("Encryption support", info.Version is >= 0 and <= 4,
                info.Version is >= 0 and <= 4 ? $"NPD version {info.Version} is supported."
                    : $"NPD version {info.Version} is unsupported."));
            checks.Add(LicenseCheck(info.ContentId, info.NeedsKlicensee, rapOverridePath, rapDirectory));
            expected = info.FileSize;
        }
        checks.Add(Pass("Expected output", $"Expected plaintext size: approximately {FormatBytes(expected)}."));
        return Complete("Decrypt EDAT / SDAT", inputPath, outputPath, expected,
            outputIsDirectory: false, checks);
    }

    public static OperationPreflightReport SelfDecrypt(string inputPath, string outputPath,
        string? rapOverridePath, string? rapDirectory, bool thenFakeSign)
    {
        using var input = File.OpenRead(inputPath);
        SelfInfo info = SelfReader.ParseInfo(input);
        bool supported = info.IsLikelyFakeSigned || SelfKeyset.Find(info.RawProgramType, info.KeyRevision) is not null;
        bool needsRap = info.Npdrm is { LicenseType: NpdrmLicenseType.Local or NpdrmLicenseType.Network };
        long expected = info.DataLength <= long.MaxValue ? (long)info.DataLength : input.Length;
        var checks = new List<PreflightCheck>
        {
            Pass("File type", $"PS3 {info.ProgramTypeText} SELF detected; revision 0x{info.KeyRevision:X4}."),
            Check("SELF key", supported,
                supported ? info.IsLikelyFakeSigned ? "Fake-signed SELF needs no retail keyset."
                    : $"SELF keyset revision 0x{info.KeyRevision:X4} is available."
                    : $"No SELF keyset is available for {info.ProgramTypeText} revision 0x{info.KeyRevision:X4}."),
            Check("Encryption support", supported,
                supported ? "This SELF encryption profile is supported." : "This SELF encryption profile is unsupported."),
            LicenseCheck(info.Npdrm?.ContentId, needsRap, rapOverridePath, rapDirectory),
            Pass("Expected output", $"Expected {(thenFakeSign ? "ELF plus fSELF" : "ELF")} payload: approximately {FormatBytes(expected)}."),
        };
        long totalExpected = thenFakeSign && expected <= long.MaxValue / 2 ? expected * 2 : expected;
        return Complete(thenFakeSign ? "Decrypt and fake-sign SELF" : "Decrypt SELF", inputPath,
            outputPath, totalExpected, outputIsDirectory: false, checks);
    }

    private static OperationPreflightReport Complete(string operation, string inputPath, string outputPath,
        long expectedBytes, bool outputIsDirectory, List<PreflightCheck> checks)
    {
        string input = Path.GetFullPath(inputPath);
        string output = Path.GetFullPath(outputPath);
        if (!outputIsDirectory && PathsEqual(input, output))
            checks.Add(Error("Output path", "The output path is the same as the input path."));
        else
        {
            string? directory = outputIsDirectory ? output : Path.GetDirectoryName(output);
            string? existingDirectory = directory;
            while (outputIsDirectory && existingDirectory is not null && !Directory.Exists(existingDirectory))
                existingDirectory = Path.GetDirectoryName(existingDirectory);
            bool exists = existingDirectory is not null && Directory.Exists(existingDirectory);
            checks.Add(Check("Output path", exists,
                exists ? outputIsDirectory && !PathsEqual(existingDirectory!, output)
                    ? $"Output directory will be created under: {existingDirectory}"
                    : $"Output location is available: {existingDirectory}"
                    : $"Output directory does not exist: {directory}"));
            if (!outputIsDirectory && File.Exists(output))
                checks.Add(Warning("Existing output", "The selected output file already exists and will be replaced."));
        }

        long? available = AvailableSpace(output);
        long workingReserve = Math.Max(64L * 1024 * 1024, expectedBytes / 10);
        long required = expectedBytes <= long.MaxValue - workingReserve ? expectedBytes + workingReserve : long.MaxValue;
        if (available is long free)
            checks.Add(Check("Disk space", free >= required,
                free >= required
                    ? $"{FormatBytes(free)} free; approximately {FormatBytes(required)} required including working space."
                    : $"Only {FormatBytes(free)} free; approximately {FormatBytes(required)} required including working space."));
        else
            checks.Add(Warning("Disk space", "Free space could not be determined for the selected output location."));

        return new OperationPreflightReport
        {
            Operation = operation,
            InputPath = input,
            OutputPath = output,
            ExpectedOutputBytes = expectedBytes,
            AvailableBytes = available,
            Checks = checks,
        };
    }

    private static PreflightCheck LicenseCheck(string? contentId, bool required,
        string? overridePath, string? rapDirectory)
    {
        if (!required)
            return Pass("License / RAP", "No RAP is required for this content.");
        if (ValidRapFile(overridePath))
            return Pass("License / RAP", $"Valid RAP override selected for {contentId}.");
        if (!string.IsNullOrWhiteSpace(contentId))
        {
            try
            {
                if (RapStore.Find(contentId, rapDirectory) is not null)
                    return Pass("License / RAP", $"A valid RAP for {contentId} is installed in the library.");
            }
            catch (ArgumentException) { }
        }
        return Error("License / RAP", $"A valid RAP is required for {contentId ?? "this licensed content"}, but none was found.");
    }

    private static long SumEntryBytes(PkgInfo info)
    {
        long total = 0;
        foreach (PkgEntry entry in info.Entries.Where(entry => entry.IsFile))
            total = checked(total + (long)entry.FileSize);
        return total;
    }

    private static bool ValidRapFile(string? path) => path is not null && File.Exists(path) && new FileInfo(path).Length == 16;
    private static PreflightCheck Check(string name, bool pass, string details) => pass ? Pass(name, details) : Error(name, details);
    private static PreflightCheck Pass(string name, string details) => new(name, PreflightCheckStatus.Pass, details);
    private static PreflightCheck Warning(string name, string details) => new(name, PreflightCheckStatus.Warning, details);
    private static PreflightCheck Error(string name, string details) => new(name, PreflightCheckStatus.Error, details);

    private static byte[] ReadPrefix(Stream stream, int count)
    {
        stream.Position = 0;
        var bytes = new byte[(int)Math.Min(count, stream.Length)];
        stream.ReadExactly(bytes);
        stream.Position = 0;
        return bytes;
    }

    private static long? AvailableSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}
