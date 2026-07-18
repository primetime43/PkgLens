using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Vita;

namespace PkgLens.Core.Shared;

public enum PackageRecommendedAction
{
    ExportPsp,
    ExportPs2Classic,
    ExportVita,
    ConvertCfw,
    ExtractAll,
    Verify,
    KeyLicenseAudit,
    AnalyzeFirmware,
}

public sealed record PackageActionRecommendation(
    PackageRecommendedAction Action,
    string Title,
    string Description,
    string Badge,
    bool IsPrimary = false);

public sealed record PackageRecommendationSet(
    string Classification,
    string Summary,
    IReadOnlyList<PackageActionRecommendation> Actions);

/// <summary>Classifies an opened package and recommends only operations supported by its format.</summary>
public static class PackageRecommendationEngine
{
    public static PackageRecommendationSet Analyze(PkgInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.IsDecrypted)
            return HeaderOnly(info);

        if (info.Header.IsPspPsVita && info.Header.PspKeyType == 1)
            return Psp(info);
        if (info.Header.IsPspPsVita && info.Header.PspKeyType is >= 2 and <= 4)
            return Vita(info);
        if (info.Header.IsPs3)
            return Ps3(info);

        return Generic(info, $"Unsupported package platform 0x{info.Header.RawPlatform:X4}");
    }

    private static PackageRecommendationSet HeaderOnly(PkgInfo info)
    {
        string classification = $"{info.Header.PlatformDisplay} package — contents unavailable";
        return new(classification,
            info.DecryptionNote ?? "PkgLens could read the header but could not decrypt the package contents.",
            new[]
            {
                new PackageActionRecommendation(PackageRecommendedAction.KeyLicenseAudit,
                    "Check keys and licenses", "Identify the required package key and unsupported encryption.",
                    "RECOMMENDED", true),
                new PackageActionRecommendation(PackageRecommendedAction.Verify,
                    "Verify package", "Check the cleartext header and structural integrity that remain available.",
                    "READ-ONLY"),
            });
    }

    private static PackageRecommendationSet Psp(PkgInfo info)
    {
        PspExportEligibility eligibility = PspPackageExporter.CheckEligibility(info);
        var actions = new List<PackageActionRecommendation>();
        if (eligibility.CanExport)
        {
            actions.Add(new(PackageRecommendedAction.ExportPsp, "Export PSP game",
                "Create EBOOT.PBP, a decrypted ISO, or a PSP-compatible CSO directly.", "RECOMMENDED", true));
        }
        actions.Add(new(PackageRecommendedAction.ExtractAll, "Extract package files",
            "Extract the original PSP package directory without converting it.", "FOLDER",
            IsPrimary: !eligibility.CanExport));
        AddLicenseAudit(info, actions);
        actions.Add(Verify());

        return new("PSP package", eligibility.Reason, actions);
    }

    private static PackageRecommendationSet Vita(PkgInfo info)
    {
        try
        {
            VitaPackageDetails details = VitaPackageExporter.Inspect(info);
            var actions = new List<PackageActionRecommendation>
            {
                new(PackageRecommendedAction.ExportVita, $"Export Vita {details.Kind.ToString().ToLowerInvariant()}",
                    $"Create the correct {details.RelativeRoot} directory structure.", "RECOMMENDED", true),
                new(PackageRecommendedAction.ExtractAll, "Extract raw package files",
                    "Extract the package without creating Vita app/patch/addcont layout.", "FOLDER"),
            };
            if (details.RequiresLicense)
                actions.Add(new(PackageRecommendedAction.KeyLicenseAudit, "Check Vita license readiness",
                    "Review package encryption and the license material still required by inner Vita content.", "LICENSE"));
            actions.Add(Verify());
            return new($"PSVita {details.Kind.ToString().ToLowerInvariant()} package",
                details.RequiresLicense
                    ? $"Detected Vita {details.Kind}; export is supported, but inner content still needs matching work.bin/zRIF material."
                    : $"Detected Vita {details.Kind}; PkgLens can export it to {details.RelativeRoot}.",
                actions);
        }
        catch (PkgFormatException ex)
        {
            return Generic(info, $"PSVita package: {ex.Message}");
        }
    }

    private static PackageRecommendationSet Ps3(PkgInfo info)
    {
        string classification = info.Metadata.ContentType switch
        {
            PkgContentType.GameExec => "PS3 game/executable package",
            PkgContentType.GameData => "PS3 game data or update package",
            PkgContentType.Theme => "PS3 theme package",
            PkgContentType.License => "PS3 license package",
            PkgContentType.Ps1Emu => "PS1 Classic package",
            PkgContentType.Ps2Classic => "PS2 Classic package",
            { } contentType => $"PS3 {contentType} package",
            null => "PS3 package",
        };
        bool hasExecutables = info.Entries.Any(entry => entry.IsFile && IsPs3Executable(entry.Name));
        bool canConvertForCfw = hasExecutables && info.Header.IsRetail;
        var actions = new List<PackageActionRecommendation>();

        if (info.Metadata.ContentType == PkgContentType.Ps2Classic)
        {
            bool hasImage = info.Entries.Any(entry => entry.IsFile &&
                Path.GetFileName(entry.Name).Equals("ISO.BIN.ENC", StringComparison.OrdinalIgnoreCase));
            if (hasImage)
                actions.Add(new(PackageRecommendedAction.ExportPs2Classic, "Export PS2 Classic",
                    "Resolve the RAP automatically, verify and decrypt ISO.BIN.ENC, then optionally rebuild a CFW/HEN package copy.",
                    "RECOMMENDED", true));
            actions.Add(new(PackageRecommendedAction.ExtractAll, "Extract package files",
                "Extract the original PS2 Classic package directory without decrypting the disc image.", "FOLDER",
                IsPrimary: !hasImage));
            AddLicenseAudit(info, actions);
            actions.Add(Verify());
            return new(classification,
                hasImage ? "ISO.BIN.ENC was detected; direct ISO export and CFW/HEN conversion are available."
                    : "No ISO.BIN.ENC disc image was found; raw extraction is available.", actions);
        }

        if (canConvertForCfw)
        {
            actions.Add(new(PackageRecommendedAction.ConvertCfw, "Convert package for CFW",
                "Process embedded EBOOT/SELF/SPRX files, resolve RAPs, fake-sign them, and rebuild the package.",
                "RECOMMENDED", true));
        }
        actions.Add(new(PackageRecommendedAction.ExtractAll,
            info.Metadata.ContentType == PkgContentType.GameData ? "Extract update/game data" : "Extract package files",
            "Rebuild the package directory tree on disk without modifying its contents.", "FOLDER",
            IsPrimary: !canConvertForCfw));
        if (hasExecutables)
            actions.Add(new(PackageRecommendedAction.AnalyzeFirmware, "Analyze required firmware",
                "Scan every EBOOT, SELF, and SPRX; report the highest requirement and verified patch support.",
                "READ-ONLY"));
        AddLicenseAudit(info, actions);
        actions.Add(Verify());

        string summary = canConvertForCfw
            ? "One or more PS3 executables were detected; firmware analysis and CFW conversion are available."
            : hasExecutables
                ? "This non-finalized package already uses the debug package path; extraction is recommended instead of retail CFW conversion."
            : $"No convertible EBOOT/SELF/SPRX was detected; extraction is the safest next action for this {classification.ToLowerInvariant()}.";
        return new(classification, summary, actions);
    }

    private static PackageRecommendationSet Generic(PkgInfo info, string summary)
    {
        var actions = new List<PackageActionRecommendation>
        {
            new(PackageRecommendedAction.ExtractAll, "Extract package files",
                "Extract all readable package entries to a folder.", "RECOMMENDED", true),
        };
        AddLicenseAudit(info, actions);
        actions.Add(Verify());
        return new(info.Header.PlatformDisplay + " package", summary, actions);
    }

    private static void AddLicenseAudit(PkgInfo info, List<PackageActionRecommendation> actions)
    {
        if (info.Metadata.DrmType is uint drm && drm != (uint)PkgDrmType.Free)
            actions.Add(new(PackageRecommendedAction.KeyLicenseAudit, "Check license readiness",
                $"Package metadata reports {DrmType.Name(drm)} DRM; check RAPs and inner content licenses.", "LICENSE"));
    }

    private static PackageActionRecommendation Verify() => new(PackageRecommendedAction.Verify,
        "Verify package integrity", "Check header authentication, signatures, and structural bounds.", "READ-ONLY");

    private static bool IsPs3Executable(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".self", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".sprx", StringComparison.OrdinalIgnoreCase);
    }
}
