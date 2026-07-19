namespace PkgLens.Core.Shared;

public static class FeatureText
{
    public const string HomeIntro = "PS3 / PSP / PS Vita package and EBOOT tools with bundled public decryption keys. Each tool is tagged with the file it works on and where its output runs.";
    public const string OpenPackage = "Browse, list, search, view, and extract a package.";
    public const string ScanFolder = "Catalog and classify every .pkg in a directory.";
    public const string BatchCenter = "Audit, classify, verify, extract, export, or CFW-convert a package folder with resumable jobs.";
    public const string PackageOrganizer = "Find duplicates and older updates, then preview a PS3 / PSP / Vita library layout.";
    public const string GameFolderInfo = "Read a content folder's PARAM.SFO and executable metadata.";
    public const string KeyLicenseAudit = "Find required keys, SELF revisions, licenses, missing RAPs, and unsupported encryption.";
    public const string PackFolder = "Build a streaming package from a content folder.";
    public const string PackPageIntro = "Build a package from a content folder. Fields are pre-filled from PARAM.SFO where possible.";
    public const string TargetCompatibility = "Check RPCS3, CEX CFW, DEX, or HEN compatibility before rebuilding.";
    public const string ConvertCfw = "Process embedded EBOOT, SELF, and SPRX files, resolve RAPs, fake-sign them, and rebuild a verified package copy.";
    public const string ExportPsp = "Locate EBOOT.PBP and export it or decrypt its disc image in one step.";
    public const string ExportPs1 = "Resolve licenses, preserve metadata, and reconstruct single- or multi-disc images.";
    public const string ExportPs2 = "Resolve the RAP, verify and decrypt ISO.BIN.ENC, then optionally rebuild a CFW/HEN package copy.";
    public const string ExportVita = "Identify app, update, DLC, or theme content and create the correct Vita directory tree.";
    public const string InspectSelf = "Read type, key revision, firmware, controls, and segment compression.";
    public const string DecryptSelf = "Decrypt EBOOT.BIN or .self to a plaintext ELF.";
    public const string FakeSignSelf = "Create a compressed CFW-ready fSELF from a plain ELF.";
    public const string PatchFirmware = "Lower a verified firmware requirement and rebuild a fake-signed copy.";
    public const string AnalyzeFirmware = "Scan every EBOOT, SELF, and SPRX; report the highest requirement and verified patch support.";
    public const string BytePatch = "Apply hex find/replace or an at-offset edit.";
    public const string DecryptEdat = "Decrypt and integrity-check an NPDRM EDAT or SDAT file.";
    public const string DecryptPageIntro = "Decrypt an NPDRM data file. Licensed EDATs resolve RAPs automatically from the library.";
    public const string ManageKeys = "Review bundled keys, import an override, and configure RAP discovery.";
    public const string VerifyPackage = "Check header authentication, signatures, decrypted item tables, and structural bounds.";
    public const string DexLimitation = "DEX compatibility can be analyzed, but genuine DEX/OFW SELF resigning requires Sony signing keys and remains out of scope.";
    public const string CliSummary = "pkglens — inspect, verify, extract, convert, and rebuild PS3, PSP, and PSVita packages";
}
