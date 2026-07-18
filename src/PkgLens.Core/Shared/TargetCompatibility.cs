using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public enum TargetCompatibilityProfile
{
    Rpcs3,
    CexCfw,
    Dex,
    Hen,
}

public enum TargetCompatibilityStatus
{
    Pass,
    Warning,
    Error,
}

public sealed record TargetCompatibilityProfileInfo(
    TargetCompatibilityProfile Profile,
    string Name,
    string ShortDescription,
    string OutputDescription)
{
    public override string ToString() => Name;
}

public sealed record TargetCompatibilityCheck(
    string Name,
    TargetCompatibilityStatus Status,
    string Details);

public sealed class TargetCompatibilityReport
{
    public required TargetCompatibilityProfileInfo Target { get; init; }
    public required string InputPath { get; init; }
    public required string SourcePlatform { get; init; }
    public required PkgFinalization SourceFinalization { get; init; }
    public required IReadOnlyList<TargetCompatibilityCheck> Checks { get; init; }

    public int ErrorCount => Checks.Count(check => check.Status == TargetCompatibilityStatus.Error);
    public int WarningCount => Checks.Count(check => check.Status == TargetCompatibilityStatus.Warning);
    public bool IsCompatible => ErrorCount == 0;
}

public static class TargetCompatibilityAnalyzer
{
    public static IReadOnlyList<TargetCompatibilityProfileInfo> Profiles { get; } =
    [
        new(TargetCompatibilityProfile.Rpcs3, "RPCS3 emulator",
            "Self-contained emulator output; retail or debug PS3 package layouts are accepted by this workflow.",
            "PkgLens fake-signs supported executables and rebuilds the package for installation in RPCS3. Game-specific emulator compatibility still varies."),
        new(TargetCompatibilityProfile.CexCfw, "CEX custom firmware",
            "Retail PS3 package layout with fake-signed executables for a jailbroken retail console.",
            "The rebuilt retail-format package is unsigned and relies on CFW signature bypasses. It will not install on stock OFW."),
        new(TargetCompatibilityProfile.Dex, "DEX debug / reference",
            "Official debug firmware expects debug-compatible package and executable signing.",
            "This conversion is unavailable: PkgLens creates CFW-style fSELF, not genuine DEX debug-signed SELF. Use a debug package only when its executables are already DEX-compatible."),
        new(TargetCompatibilityProfile.Hen, "PS3HEN",
            "Retail game/update package output for HEN-enabled CEX systems; system and kernel payloads are excluded.",
            "PkgLens checks the package structure and content scope. The console's installed HEN/HFW version and game-specific behavior must still be tested on the target system."),
    ];

    public static TargetCompatibilityProfileInfo Describe(TargetCompatibilityProfile profile) =>
        Profiles.Single(item => item.Profile == profile);

    public static TargetCompatibilityReport AnalyzeCfwConversion(string inputPath, IKeyProvider keys,
        string? rapDirectory, TargetCompatibilityProfile profile,
        CfwFirmwareTarget? firmwareTarget = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(keys);
        string fullPath = Path.GetFullPath(inputPath);
        using var package = File.OpenRead(fullPath);
        PkgInfo info = PkgReader.Read(package, keys);
        cancellationToken.ThrowIfCancellationRequested();

        var checks = new List<TargetCompatibilityCheck>
        {
            Check("Package platform", info.Header.IsPs3,
                info.Header.IsPs3 ? "PS3 package detected."
                    : $"This is {info.Header.PlatformDisplay}; the conversion supports PS3 packages only."),
            Check("Package decryption", info.IsDecrypted,
                info.IsDecrypted ? "The package item table and files can be decrypted."
                    : info.DecryptionNote ?? "The package key could not be resolved."),
        };

        int executableCount = info.IsDecrypted
            ? info.Entries.Count(entry => entry.IsFile && IsExecutable(entry.Name))
            : 0;
        checks.Add(Check("Convertible executables", executableCount > 0,
            executableCount > 0
                ? $"Found {executableCount} EBOOT.BIN, SELF, or SPRX executable(s)."
                : "No EBOOT.BIN, SELF, or SPRX executables were found."));

        AddTargetRules(info, profile, checks);

        KeyLicenseAuditReport audit = KeyLicenseAudit.Inspect(fullPath, keys,
            new KeyLicenseAuditOptions { RapDirectory = rapDirectory }, cancellationToken);
        checks.Add(audit.UnsupportedCount == 0 && audit.ErrorCount == 0
            ? Pass("Encryption support", "All detected package and SELF key revisions are supported.")
            : Error("Encryption support",
                $"{audit.UnsupportedCount} unsupported encryption item(s) and {audit.ErrorCount} audit error(s) were detected."));
        checks.Add(audit.MissingRapCount == 0
            ? Pass("RAP / licenses", "Every licensed executable has an available RAP, or no RAP is required.")
            : Error("RAP / licenses",
                $"{audit.MissingRapCount} required RAP(s) are missing. Import them before conversion so every executable can be decrypted and fake-signed."));

        checks.Add(firmwareTarget is null
            ? Warning("Firmware requirement",
                "Executable firmware requirements will be preserved. The selected target must meet the highest existing SELF/SDK requirement.")
            : Warning("Firmware requirement",
                $"PkgLens will target {firmwareTarget} only where a verified sys_process_param marker can be safely lowered. Other firmware or hardware requirements are not removed."));

        TargetCompatibilityProfileInfo target = Describe(profile);
        checks.Add(profile == TargetCompatibilityProfile.Dex
            ? Error("Output signing model", target.OutputDescription)
            : Pass("Output signing model", target.OutputDescription));

        return new TargetCompatibilityReport
        {
            Target = target,
            InputPath = fullPath,
            SourcePlatform = info.Header.PlatformDisplay,
            SourceFinalization = info.Header.Finalization,
            Checks = checks,
        };
    }

    public static void ValidateConversionProfile(PkgInfo info, TargetCompatibilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (profile == TargetCompatibilityProfile.Dex)
            throw new PkgFormatException(
                "DEX output is not supported by one-click conversion because PkgLens creates CFW-style fake-signed SELF, not genuine debug-signed SELF.");
        if (profile is TargetCompatibilityProfile.CexCfw or TargetCompatibilityProfile.Hen &&
            info.Header.Finalization != PkgFinalization.Retail)
            throw new PkgFormatException(
                $"{Describe(profile).Name} output requires a retail PS3 package layout; the source is {info.Header.Finalization}.");
        if (profile == TargetCompatibilityProfile.Hen && IsSystemContent(info))
            throw new PkgFormatException(
                "PS3HEN conversion is limited to game and update packages; system, VSH, dev_flash, and kernel-oriented content is blocked.");
    }

    private static void AddTargetRules(PkgInfo info, TargetCompatibilityProfile profile,
        ICollection<TargetCompatibilityCheck> checks)
    {
        bool retail = info.Header.Finalization == PkgFinalization.Retail;
        bool debug = info.Header.Finalization == PkgFinalization.Debug;
        bool systemContent = IsSystemContent(info);
        switch (profile)
        {
            case TargetCompatibilityProfile.Rpcs3:
                checks.Add(Check("Package layout", retail || debug,
                    retail || debug
                        ? $"{info.Header.Finalization} PS3 package layout can be rebuilt for RPCS3."
                        : $"Unknown package finalization 0x{info.Header.RawFinalization:X4} is unsupported."));
                checks.Add(systemContent
                    ? Warning("Content scope",
                        "VSH/system content was detected. RPCS3 can run VSH with limitations, so this package needs manual emulator testing.")
                    : Pass("Content scope", "Game or update content is suitable for the normal RPCS3 package workflow."));
                break;
            case TargetCompatibilityProfile.CexCfw:
                checks.Add(Check("Package layout", retail,
                    retail ? "Retail package layout is suitable for CEX CFW."
                        : $"CEX CFW conversion requires a retail package layout; this source is {info.Header.Finalization}."));
                checks.Add(systemContent
                    ? Warning("Content scope",
                        "System/VSH content was detected. CFW system modifications are high risk and cannot be declared generally safe by PkgLens.")
                    : Pass("Content scope", "Game or update content is within the normal CEX CFW conversion scope."));
                break;
            case TargetCompatibilityProfile.Dex:
                checks.Add(Check("Package layout", debug,
                    debug ? "The outer package uses a debug layout."
                        : $"DEX official/debug firmware expects a debug package layout; this source is {info.Header.Finalization}."));
                checks.Add(Error("Executable signing",
                    "One-click conversion emits fake-signed CFW executables. PkgLens does not currently create genuine DEX debug-signed SELF/SPRX output."));
                break;
            case TargetCompatibilityProfile.Hen:
                checks.Add(Check("Package layout", retail,
                    retail ? "Retail CEX package layout is suitable for the HEN package workflow."
                        : $"HEN conversion requires a retail CEX package layout; this source is {info.Header.Finalization}."));
                checks.Add(Check("Content scope", !systemContent,
                    systemContent
                        ? "System, VSH, dev_flash, or kernel-oriented content was detected. PkgLens limits HEN conversion to game and update packages."
                        : "No system or kernel-oriented paths were detected; game/update content is within the supported HEN scope."));
                checks.Add(Warning("Installed HEN environment",
                    "PkgLens cannot inspect the console. Confirm that HEN is enabled and the installed HFW/HEN version supports the target console firmware."));
                break;
        }
    }

    private static bool IsSystemContent(PkgInfo info)
    {
        if (info.Metadata.ContentType is PkgContentType.Vsh or PkgContentType.VshAvc)
            return true;
        string installDirectory = info.Metadata.InstallDirectory ?? string.Empty;
        if (installDirectory.StartsWith("dev_flash", StringComparison.OrdinalIgnoreCase) ||
            installDirectory.StartsWith("vsh", StringComparison.OrdinalIgnoreCase))
            return true;
        return info.Entries.Any(entry =>
        {
            string path = entry.Name.Replace('\\', '/').TrimStart('/');
            return path.StartsWith("dev_flash/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("vsh/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("lv2/", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/stage2", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsExecutable(string path)
    {
        string name = Path.GetFileName(path.Replace('\\', '/'));
        return name.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".self", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".sprx", StringComparison.OrdinalIgnoreCase);
    }

    private static TargetCompatibilityCheck Check(string name, bool pass, string details) =>
        pass ? Pass(name, details) : Error(name, details);
    private static TargetCompatibilityCheck Pass(string name, string details) =>
        new(name, TargetCompatibilityStatus.Pass, details);
    private static TargetCompatibilityCheck Warning(string name, string details) =>
        new(name, TargetCompatibilityStatus.Warning, details);
    private static TargetCompatibilityCheck Error(string name, string details) =>
        new(name, TargetCompatibilityStatus.Error, details);
}
