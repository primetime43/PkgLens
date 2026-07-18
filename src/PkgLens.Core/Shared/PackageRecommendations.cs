using PkgLens.Core.Psp;
using PkgLens.Core.Ps1;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Vita;

namespace PkgLens.Core.Shared;

public enum PackageRecommendedAction
{
    ExportPsp,
    ExportPs1Classic,
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
    bool IsPrimary = false)
{
    public string Tooltip => OperationCatalog.ForRecommendation(Action).Tooltip;
}

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
                Recommend(PackageRecommendedAction.KeyLicenseAudit, "RECOMMENDED", true),
                Recommend(PackageRecommendedAction.Verify, "READ-ONLY",
                    title: "Verify package",
                    description: "Check the cleartext header and structural integrity that remain available."),
            });
    }

    private static PackageRecommendationSet Psp(PkgInfo info)
    {
        OperationEligibilityResult eligibility = OperationCatalog.CheckSuggested(PackageRecommendedAction.ExportPsp, info);
        var actions = new List<PackageActionRecommendation>();
        if (eligibility.Eligible)
            actions.Add(Recommend(PackageRecommendedAction.ExportPsp, "RECOMMENDED", true));
        actions.Add(Recommend(PackageRecommendedAction.ExtractAll, "FOLDER",
            IsPrimary: !eligibility.Eligible,
            description: "Extract the original PSP package directory without converting it."));
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
                Recommend(PackageRecommendedAction.ExportVita, "RECOMMENDED", true,
                    title: $"Export Vita {details.Kind.ToString().ToLowerInvariant()}",
                    description: $"Create the correct {details.RelativeRoot} directory structure."),
                Recommend(PackageRecommendedAction.ExtractAll, "FOLDER",
                    title: "Extract raw package files",
                    description: "Extract the package without creating Vita app/patch/addcont layout."),
            };
            if (details.RequiresLicense)
                actions.Add(Recommend(PackageRecommendedAction.KeyLicenseAudit, "LICENSE",
                    title: "Check Vita license readiness",
                    description: "Review package encryption and the license material still required by inner Vita content."));
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
        bool hasExecutables = OperationCatalog.CheckSuggested(PackageRecommendedAction.AnalyzeFirmware, info).Eligible;
        bool canConvertForCfw = OperationCatalog.CheckSuggested(PackageRecommendedAction.ConvertCfw, info).Eligible;
        var actions = new List<PackageActionRecommendation>();

        if (info.Metadata.ContentType == PkgContentType.Ps1Emu)
        {
            bool hasImage = OperationCatalog.CheckSuggested(PackageRecommendedAction.ExportPs1Classic, info).Eligible;
            if (hasImage)
                actions.Add(Recommend(PackageRecommendedAction.ExportPs1Classic, "RECOMMENDED", true));
            actions.Add(Recommend(PackageRecommendedAction.ExtractAll, "FOLDER",
                IsPrimary: !hasImage,
                description: "Extract the original PS1 Classic package directory without reconstructing its disc image."));
            AddLicenseAudit(info, actions);
            actions.Add(Verify());
            return new(classification,
                hasImage ? "A PS1 Classic image container was detected; guided metadata and BIN/CUE export are available."
                    : "No EBOOT.PBP or ISO.BIN.EDAT image container was found; raw extraction is available.", actions);
        }

        if (info.Metadata.ContentType == PkgContentType.Ps2Classic)
        {
            bool hasImage = OperationCatalog.CheckSuggested(PackageRecommendedAction.ExportPs2Classic, info).Eligible;
            if (hasImage)
                actions.Add(Recommend(PackageRecommendedAction.ExportPs2Classic, "RECOMMENDED", true));
            actions.Add(Recommend(PackageRecommendedAction.ExtractAll, "FOLDER",
                IsPrimary: !hasImage,
                description: "Extract the original PS2 Classic package directory without decrypting the disc image."));
            AddLicenseAudit(info, actions);
            actions.Add(Verify());
            return new(classification,
                hasImage ? "ISO.BIN.ENC was detected; direct ISO export and CFW/HEN conversion are available."
                    : "No ISO.BIN.ENC disc image was found; raw extraction is available.", actions);
        }

        if (canConvertForCfw)
        {
            actions.Add(Recommend(PackageRecommendedAction.ConvertCfw, "RECOMMENDED", true));
        }
        actions.Add(Recommend(PackageRecommendedAction.ExtractAll, "FOLDER",
            IsPrimary: !canConvertForCfw,
            title: info.Metadata.ContentType == PkgContentType.GameData ? "Extract update/game data" : null));
        if (hasExecutables)
            actions.Add(Recommend(PackageRecommendedAction.AnalyzeFirmware, "READ-ONLY"));
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
            Recommend(PackageRecommendedAction.ExtractAll, "RECOMMENDED", true,
                description: "Extract all readable package entries to a folder."),
        };
        AddLicenseAudit(info, actions);
        actions.Add(Verify());
        return new(info.Header.PlatformDisplay + " package", summary, actions);
    }

    private static void AddLicenseAudit(PkgInfo info, List<PackageActionRecommendation> actions)
    {
        if (OperationCatalog.CheckSuggested(PackageRecommendedAction.KeyLicenseAudit, info).Eligible &&
            info.Metadata.DrmType is uint drm)
            actions.Add(Recommend(PackageRecommendedAction.KeyLicenseAudit, "LICENSE",
                title: "Check license readiness",
                description: $"Package metadata reports {DrmType.Name(drm)} DRM; check RAPs and inner content licenses."));
    }

    private static PackageActionRecommendation Verify() =>
        Recommend(PackageRecommendedAction.Verify, "READ-ONLY");

    private static PackageActionRecommendation Recommend(PackageRecommendedAction action, string badge,
        bool IsPrimary = false, string? title = null, string? description = null)
    {
        OperationDefinition operation = OperationCatalog.ForRecommendation(action);
        return new(action, title ?? operation.Name, description ?? operation.Description, badge, IsPrimary);
    }
}
