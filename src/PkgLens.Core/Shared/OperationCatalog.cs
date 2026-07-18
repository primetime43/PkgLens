using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Vita;

namespace PkgLens.Core.Shared;

public enum OperationId
{
    OpenPackage,
    ClosePackage,
    PackFolder,
    ComparePackages,
    ConvertCfw,
    ExportPs1Classic,
    ExportPs2Classic,
    ExportPsp,
    ExportPspPbp,
    ExportPspIso,
    ExportPspCso,
    ExportVita,
    BrowsePsarc,
    ExtractAll,
    SavePackageAs,
    Exit,
    EditSfo,
    ReplaceSelectedFile,
    ExtractSelectedFile,
    ViewSelectedFile,
    AnalyzeFirmware,
    AnalyzeFirmwareFolder,
    PatchFirmware,
    BytePatch,
    VerifyPackage,
    PackageInfo,
    FolderInfo,
    KeyLicenseAudit,
    ScanLibrary,
    BatchCenter,
    PackageOrganizer,
    ImportOverrideKey,
    KeysFolder,
    RapLibrary,
    ClassifyLibrary,
}

public enum OperationMenu
{
    File,
    Edit,
    Tools,
}

public enum OperationEligibilityRule
{
    Never,
    Always,
    HasPackage,
    DecryptedPackage,
    SelectedFile,
    EditableSfo,
    PspExportable,
    Ps1ClassicExportable,
    Ps2ClassicExportable,
    VitaExportable,
    RetailPs3Executable,
    Ps3Executable,
    LicensedPackage,
}

public sealed record OperationMenuLocation(
    OperationMenu Menu,
    int Section,
    int Order,
    string? Group = null);

public sealed record OperationMenuGroup(
    string Id,
    OperationMenu Menu,
    int Section,
    int Order,
    string Header);

public sealed record OperationEligibilityResult(bool Eligible, string Reason);

public sealed record OperationDefinition(
    OperationId Id,
    string Name,
    string Description,
    string Tooltip,
    string MenuHeader,
    OperationEligibilityRule MenuEligibility,
    OperationEligibilityRule SuggestedEligibility,
    OperationEligibilityRule BatchEligibility,
    IReadOnlyList<OperationMenuLocation> MenuLocations,
    PackageRecommendedAction? RecommendationAction = null,
    BatchOperation? BatchOperation = null,
    string? BatchLabelOverride = null,
    string? BatchDescriptionOverride = null)
{
    public string BatchLabel => BatchLabelOverride ?? Name;
    public string BatchDescription => BatchDescriptionOverride ?? Description;
}

public static class OperationCatalog
{
    private static readonly OperationMenuLocation[] NoMenu = [];

    public static IReadOnlyList<OperationMenuGroup> MenuGroups { get; } =
    [
        new("BuildConvert", OperationMenu.File, 1, 10, "_Build and convert"),
        new("PlatformExport", OperationMenu.File, 1, 20, "_Export platform content"),
        new("ExtractRepack", OperationMenu.File, 1, 30, "_Extract and repack"),
        new("FirmwareCfw", OperationMenu.Tools, 1, 10, "_Firmware and CFW"),
        new("InspectVerify", OperationMenu.Tools, 1, 20, "_Inspect and verify"),
        new("LibraryBatch", OperationMenu.Tools, 1, 30, "_Library and batch"),
        new("KeysLicenses", OperationMenu.Tools, 1, 40, "_Keys and licenses"),
    ];

    public static IReadOnlyList<OperationDefinition> All { get; } =
    [
        Def(OperationId.OpenPackage, "Open package", FeatureText.OpenPackage,
            "Open and inspect a PS3, PSP, or PSVita package.", "_Open package…",
            menu: [Loc(OperationMenu.File, 0, 10)]),
        Def(OperationId.ClosePackage, "Close package", "Close the currently opened package.",
            "Close the currently opened package without exiting PkgLens.", "_Close package",
            menuRule: OperationEligibilityRule.HasPackage, menu: [Loc(OperationMenu.File, 0, 20)]),
        Def(OperationId.PackFolder, "Pack folder into package", FeatureText.PackFolder,
            "Open the Pack page to build a new package from an extracted content folder.", "_Pack folder into package…",
            menu: [Loc(OperationMenu.File, 1, 10, "BuildConvert")]),
        Def(OperationId.ComparePackages, "Compare packages / create update", "Compare package contents and create a compact update or overlay.",
            "Compare base and target package files and PARAM.SFO values, then create a compact CFW/HEN or RPCS3 overlay package.",
            "_Compare packages / create update…", menu: [Loc(OperationMenu.File, 1, 20, "BuildConvert")]),
        Def(OperationId.ConvertCfw, "Convert package for CFW", FeatureText.ConvertCfw,
            "Check RPCS3, CEX CFW, DEX, or HEN compatibility before conversion starts.",
            "Convert PS3 package for a _target…", suggestedRule: OperationEligibilityRule.RetailPs3Executable,
            batchRule: OperationEligibilityRule.Ps3Executable,
            menu: [Loc(OperationMenu.File, 1, 30, "BuildConvert"), Loc(OperationMenu.Tools, 1, 20, "FirmwareCfw")],
            recommendation: PackageRecommendedAction.ConvertCfw, batch: BatchOperation.ConvertCfw,
            batchLabel: "Convert PS3 packages for CFW",
            batchDescription: "Decrypts and fake-signs embedded EBOOT/SELF/SPRX files, rebuilds the package, verifies every output, and skips incompatible packages."),
        Def(OperationId.ExportPs1Classic, "Export PS1 Classic", FeatureText.ExportPs1,
            "Identify a PS1 Classic, resolve its RAP, export metadata, and reconstruct emulator-ready BIN/CUE images.",
            "PS_1 Classic → BIN / CUE / metadata…", suggestedRule: OperationEligibilityRule.Ps1ClassicExportable,
            menu: [Loc(OperationMenu.File, 1, 10, "PlatformExport")], recommendation: PackageRecommendedAction.ExportPs1Classic),
        Def(OperationId.ExportPs2Classic, "Export PS2 Classic", FeatureText.ExportPs2,
            "Authenticate and decrypt ISO.BIN.ENC to ISO, with an optional CFW/HEN package rebuild.",
            "PS_2 Classic → ISO / CFW package…", suggestedRule: OperationEligibilityRule.Ps2ClassicExportable,
            menu: [Loc(OperationMenu.File, 1, 20, "PlatformExport")], recommendation: PackageRecommendedAction.ExportPs2Classic),
        Def(OperationId.ExportPsp, "Export PSP game", FeatureText.ExportPsp,
            "Export a PSP package directly to EBOOT.PBP, decrypted ISO, or compressed CSO.",
            "_PSP → PBP / ISO / CSO…", suggestedRule: OperationEligibilityRule.PspExportable,
            menu: [Loc(OperationMenu.File, 1, 30, "PlatformExport")], recommendation: PackageRecommendedAction.ExportPsp),
        Def(OperationId.ExportPspPbp, "Export PSP packages to PBP", FeatureText.ExportPsp,
            "Exports EBOOT.PBP from compatible PSP packages.", "", batchRule: OperationEligibilityRule.PspExportable,
            batch: BatchOperation.ExportPbp,
            batchDescription: "Exports EBOOT.PBP from PSP packages and skips PS3, Vita, or incompatible packages automatically."),
        Def(OperationId.ExportPspIso, "Export PSP packages to ISO", FeatureText.ExportPsp,
            "Decrypts PSP disc images directly to verified ISO output.", "", batchRule: OperationEligibilityRule.PspExportable,
            batch: BatchOperation.ExportIso,
            batchDescription: "Decrypts PSP disc images directly to ISO, verifies ISO9660 structure, and skips packages without a PSP disc image."),
        Def(OperationId.ExportPspCso, "Export PSP packages to CSO", FeatureText.ExportPsp,
            "Decrypts and compresses PSP disc images to verified CSO output.", "", batchRule: OperationEligibilityRule.PspExportable,
            batch: BatchOperation.ExportCso,
            batchDescription: "Decrypts and compresses PSP disc images to CSO, then decompresses every block again for verification."),
        Def(OperationId.ExportVita, "Export PSVita package", FeatureText.ExportVita,
            "Export a Vita package into the correct app, patch, or addcont directory layout.",
            "PS_Vita → app / patch / addcont…", suggestedRule: OperationEligibilityRule.VitaExportable,
            menu: [Loc(OperationMenu.File, 1, 40, "PlatformExport")], recommendation: PackageRecommendedAction.ExportVita),
        Def(OperationId.BrowsePsarc, "Browse / rebuild PSARC archive", "Browse, extract, replace, and rebuild PSARC contents.",
            "Browse, extract, queue replacements, and rebuild a verified copy of a standard zlib PSARC archive.",
            "Browse / rebuild _PSARC archive…", menu: [Loc(OperationMenu.File, 1, 10, "ExtractRepack")]),
        Def(OperationId.ExtractAll, "Extract package files", "Rebuild the package directory tree on disk without modifying its contents.",
            "Extract the currently opened package and recreate its directory tree on disk.", "Extract _all files…",
            menuRule: OperationEligibilityRule.DecryptedPackage, suggestedRule: OperationEligibilityRule.DecryptedPackage,
            batchRule: OperationEligibilityRule.DecryptedPackage,
            menu: [Loc(OperationMenu.File, 1, 20, "ExtractRepack")], recommendation: PackageRecommendedAction.ExtractAll,
            batch: BatchOperation.Extract, batchLabel: "Extract every package",
            batchDescription: "Rebuilds each decrypted package directory tree under the output folder. Packages without a usable key are skipped with an actionable reason."),
        Def(OperationId.SavePackageAs, "Save package as", "Rebuild a separate package copy containing the current edits.",
            "Rebuild a separate package containing replacements or PARAM.SFO edits. The original remains unchanged.",
            "Save package _as…", menuRule: OperationEligibilityRule.DecryptedPackage,
            menu: [Loc(OperationMenu.File, 1, 30, "ExtractRepack")]),
        Def(OperationId.Exit, "Exit PkgLens", "Close PkgLens.", "Close PkgLens.", "E_xit PkgLens",
            menu: [Loc(OperationMenu.File, 2, 10)]),
        Def(OperationId.EditSfo, "Edit PARAM.SFO", "Edit the opened package's PARAM.SFO metadata.",
            "Edit PARAM.SFO metadata in the opened package.", "Edit _PARAM.SFO…",
            menuRule: OperationEligibilityRule.EditableSfo, menu: [Loc(OperationMenu.Edit, 0, 10)]),
        Def(OperationId.ReplaceSelectedFile, "Replace selected file", "Replace the selected package entry's contents.",
            "Replace the selected file's contents before rebuilding a package copy.", "_Replace selected file…",
            menuRule: OperationEligibilityRule.SelectedFile, menu: [Loc(OperationMenu.Edit, 0, 20)]),
        Def(OperationId.ExtractSelectedFile, "Extract selected file", "Extract the selected package entry.",
            "Extract the selected file from the opened package.", "E_xtract selected file…",
            menuRule: OperationEligibilityRule.SelectedFile, menu: [Loc(OperationMenu.Edit, 1, 10)]),
        Def(OperationId.ViewSelectedFile, "View selected file", "Open the selected package entry in the built-in viewer.",
            "View the selected file without extracting it manually.", "_View selected file…",
            menuRule: OperationEligibilityRule.SelectedFile, menu: [Loc(OperationMenu.Edit, 1, 20)]),
        Def(OperationId.AnalyzeFirmware, "Analyze required firmware", FeatureText.AnalyzeFirmware,
            "Scan every SELF/SPRX, report required firmware, and offer safe package patching when supported.",
            "Analyze / patch package _firmware…", menuRule: OperationEligibilityRule.HasPackage,
            suggestedRule: OperationEligibilityRule.Ps3Executable,
            menu: [Loc(OperationMenu.Tools, 0, 10)], recommendation: PackageRecommendedAction.AnalyzeFirmware),
        Def(OperationId.AnalyzeFirmwareFolder, "Analyze extracted firmware folder", FeatureText.AnalyzeFirmware,
            "Scan SELF and SPRX files in an extracted folder and report their firmware requirements.",
            "Analyze extracted firmware _folder…", menu: [Loc(OperationMenu.Tools, 1, 10, "FirmwareCfw")]),
        Def(OperationId.PatchFirmware, "Patch SELF / EBOOT firmware", FeatureText.PatchFirmware,
            "Open the guided SELF/EBOOT firmware patch and fake-sign tool.", "Patch one _SELF / EBOOT firmware…",
            menu: [Loc(OperationMenu.Tools, 1, 30, "FirmwareCfw")]),
        Def(OperationId.BytePatch, "Byte patch SELF / EBOOT", FeatureText.BytePatch,
            "Open the advanced byte-patch tool for a single SELF or EBOOT file.", "Raw _byte patch SELF / EBOOT…",
            menu: [Loc(OperationMenu.Tools, 1, 40, "FirmwareCfw")]),
        Def(OperationId.VerifyPackage, "Verify package integrity", FeatureText.VerifyPackage,
            "Check package authentication, signatures, decrypted tables, and structural bounds.",
            "_Verify package integrity…", menuRule: OperationEligibilityRule.HasPackage,
            suggestedRule: OperationEligibilityRule.HasPackage, batchRule: OperationEligibilityRule.HasPackage,
            menu: [Loc(OperationMenu.Tools, 1, 10, "InspectVerify")], recommendation: PackageRecommendedAction.Verify,
            batch: BatchOperation.Verify,
            batchDescription: "Checks package structure, bounds, authentication, signatures, and decrypted item tables. Saves a verification report for every package."),
        Def(OperationId.PackageInfo, "Package info", "Show package header, content ID, metadata, and file counts.",
            "Show detailed information about the opened package.", "Package _info…",
            menuRule: OperationEligibilityRule.HasPackage, menu: [Loc(OperationMenu.Tools, 1, 20, "InspectVerify")]),
        Def(OperationId.FolderInfo, "Folder info", FeatureText.GameFolderInfo,
            "Inspect PARAM.SFO and executable metadata in an extracted content folder.", "_Folder info…",
            menu: [Loc(OperationMenu.Tools, 1, 30, "InspectVerify")]),
        Def(OperationId.KeyLicenseAudit, "Check keys and licenses", FeatureText.KeyLicenseAudit,
            "Report required keys, licenses, available RAPs, missing RAPs, and unsupported encryption.",
            "Key / _license audit…", suggestedRule: OperationEligibilityRule.LicensedPackage,
            menu: [Loc(OperationMenu.Tools, 1, 40, "InspectVerify")], recommendation: PackageRecommendedAction.KeyLicenseAudit,
            batch: BatchOperation.Audit, batchRule: OperationEligibilityRule.HasPackage,
            batchLabel: "Audit keys and licenses",
            batchDescription: "Reads package keys, SELF revisions, content IDs, licenses, RAP availability, and unsupported encryption. Saves one JSON report per package."),
        Def(OperationId.ScanLibrary, "Scan package library", FeatureText.ScanFolder,
            "Catalog and classify every package in a selected directory.", "Scan package _library…",
            menu: [Loc(OperationMenu.Tools, 1, 10, "LibraryBatch")]),
        Def(OperationId.BatchCenter, "Batch processing center", FeatureText.BatchCenter,
            "Prepare and resume multi-package audit, verification, extraction, export, or conversion jobs.",
            "_Batch processing center…", menu: [Loc(OperationMenu.Tools, 1, 20, "LibraryBatch")]),
        Def(OperationId.PackageOrganizer, "Duplicate / package organizer", FeatureText.PackageOrganizer,
            "Detect duplicates and superseded updates, then preview an organized library layout.",
            "Duplicate / package _organizer…", menu: [Loc(OperationMenu.Tools, 1, 30, "LibraryBatch")]),
        Def(OperationId.ImportOverrideKey, "Import override key", FeatureText.ManageKeys,
            "Import a package-key override file.", "Import _override key…",
            menu: [Loc(OperationMenu.Tools, 1, 10, "KeysLicenses")]),
        Def(OperationId.KeysFolder, "Keys folder", FeatureText.ManageKeys,
            "Open the configured keys folder.", "Keys _folder…",
            menu: [Loc(OperationMenu.Tools, 1, 20, "KeysLicenses")]),
        Def(OperationId.RapLibrary, "RAP library", FeatureText.ManageKeys,
            "Import, list, remove, and validate RAP licenses.", "_RAP library…",
            menu: [Loc(OperationMenu.Tools, 1, 30, "KeysLicenses")]),
        Def(OperationId.ClassifyLibrary, "Classify and match library", FeatureText.ScanFolder,
            "Classify base games, updates, DLC, themes, regions, and versions.", "",
            batch: BatchOperation.Classify, batchRule: OperationEligibilityRule.HasPackage,
            batchDescription: "Classifies base games, updates, DLC, themes, regions, and versions. Also writes a combined library report with missing-base and region warnings."),
    ];

    private static readonly IReadOnlyDictionary<OperationId, OperationDefinition> ById =
        All.ToDictionary(operation => operation.Id);
    private static readonly IReadOnlyDictionary<PackageRecommendedAction, OperationDefinition> ByRecommendation =
        All.Where(operation => operation.RecommendationAction is not null)
            .ToDictionary(operation => operation.RecommendationAction!.Value);
    private static readonly IReadOnlyDictionary<BatchOperation, OperationDefinition> ByBatch =
        All.Where(operation => operation.BatchOperation is not null)
            .ToDictionary(operation => operation.BatchOperation!.Value);

    public static IReadOnlyList<OperationDefinition> BatchOperations { get; } =
        Enum.GetValues<BatchOperation>().Select(ForBatch).ToArray();

    public static OperationDefinition Get(OperationId id) => ById[id];
    public static OperationDefinition ForRecommendation(PackageRecommendedAction action) => ByRecommendation[action];
    public static OperationDefinition ForBatch(BatchOperation operation) => ByBatch[operation];

    public static IEnumerable<(OperationDefinition Definition, OperationMenuLocation Location)> MenuItems(OperationMenu menu) =>
        All.SelectMany(definition => definition.MenuLocations
            .Where(location => location.Menu == menu)
            .Select(location => (definition, location)));

    public static string? MenuBindingPath(OperationEligibilityRule rule) => rule switch
    {
        OperationEligibilityRule.Always => null,
        OperationEligibilityRule.HasPackage => "HasPackage",
        OperationEligibilityRule.DecryptedPackage => "Package.IsDecrypted",
        OperationEligibilityRule.SelectedFile => "Package.HasSelectedFile",
        OperationEligibilityRule.EditableSfo => "Package.CanEditSfo",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unsupported menu eligibility rule."),
    };

    public static OperationEligibilityResult CheckSuggested(PackageRecommendedAction action, PkgInfo info) =>
        Check(ForRecommendation(action).SuggestedEligibility, info);

    public static OperationEligibilityResult CheckBatch(BatchOperation operation, PkgInfo info) =>
        Check(ForBatch(operation).BatchEligibility, info);

    private static OperationEligibilityResult Check(OperationEligibilityRule rule, PkgInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return rule switch
        {
            OperationEligibilityRule.Never => No("This operation is not available on this surface."),
            OperationEligibilityRule.Always or OperationEligibilityRule.HasPackage => Yes(),
            OperationEligibilityRule.DecryptedPackage => info.IsDecrypted
                ? Yes() : No(info.DecryptionNote ?? "Package contents could not be decrypted."),
            OperationEligibilityRule.PspExportable => PspEligibility(info),
            OperationEligibilityRule.Ps1ClassicExportable => HasNamedEntry(info, "EBOOT.PBP", "ISO.BIN.EDAT") &&
                info.Metadata.ContentType == PkgContentType.Ps1Emu
                ? Yes() : No("No supported PS1 Classic image container was found."),
            OperationEligibilityRule.Ps2ClassicExportable => HasNamedEntry(info, "ISO.BIN.ENC") &&
                info.Metadata.ContentType == PkgContentType.Ps2Classic
                ? Yes() : No("No ISO.BIN.ENC PS2 Classic image was found."),
            OperationEligibilityRule.VitaExportable => VitaEligibility(info),
            OperationEligibilityRule.RetailPs3Executable => Ps3ExecutableEligibility(info, retailRequired: true),
            OperationEligibilityRule.Ps3Executable => Ps3ExecutableEligibility(info, retailRequired: false),
            OperationEligibilityRule.LicensedPackage => !info.IsDecrypted ||
                info.Metadata.DrmType is uint drm && drm != (uint)PkgDrmType.Free
                ? Yes() : No("No licensed package content was detected."),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unsupported package eligibility rule."),
        };
    }

    private static OperationEligibilityResult PspEligibility(PkgInfo info)
    {
        PspExportEligibility eligibility = PspPackageExporter.CheckEligibility(info);
        return new(eligibility.CanExport, eligibility.Reason);
    }

    private static OperationEligibilityResult VitaEligibility(PkgInfo info)
    {
        try
        {
            VitaPackageExporter.Inspect(info);
            return Yes();
        }
        catch (PkgFormatException ex)
        {
            return No(ex.Message);
        }
    }

    private static OperationEligibilityResult Ps3ExecutableEligibility(PkgInfo info, bool retailRequired)
    {
        if (!info.Header.IsPs3)
            return No($"This is a {info.Header.PlatformDisplay} package, not a PS3 package.");
        if (retailRequired && !info.Header.IsRetail)
            return No("The PS3 package is already non-finalized; retail CFW conversion is not required.");
        return HasPs3Executable(info)
            ? Yes()
            : No("No EBOOT.BIN, SELF, or SPRX executables were found.");
    }

    private static bool HasNamedEntry(PkgInfo info, params string[] names) =>
        info.Entries.Any(entry => entry.IsFile && names.Contains(Path.GetFileName(entry.Name), StringComparer.OrdinalIgnoreCase));

    private static bool HasPs3Executable(PkgInfo info) =>
        info.Entries.Any(entry => entry.IsFile && IsPs3Executable(entry.Name));

    private static bool IsPs3Executable(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".self", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".sprx", StringComparison.OrdinalIgnoreCase);
    }

    private static OperationEligibilityResult Yes() => new(true, "Supported.");
    private static OperationEligibilityResult No(string reason) => new(false, reason);

    private static OperationMenuLocation Loc(OperationMenu menu, int section, int order, string? group = null) =>
        new(menu, section, order, group);

    private static OperationDefinition Def(
        OperationId id,
        string name,
        string description,
        string tooltip,
        string menuHeader,
        OperationEligibilityRule menuRule = OperationEligibilityRule.Always,
        OperationEligibilityRule suggestedRule = OperationEligibilityRule.Never,
        OperationEligibilityRule batchRule = OperationEligibilityRule.Never,
        IReadOnlyList<OperationMenuLocation>? menu = null,
        PackageRecommendedAction? recommendation = null,
        BatchOperation? batch = null,
        string? batchLabel = null,
        string? batchDescription = null) =>
        new(id, name, description, tooltip, menuHeader, menuRule, suggestedRule, batchRule,
            menu ?? NoMenu, recommendation, batch, batchLabel, batchDescription);
}
