using System.Text.RegularExpressions;
using PkgLens.Core.Shared;

namespace PkgLens.Cli;

/// <summary>Parsed CLI options shared by all subcommands.</summary>
internal sealed class Options
{
    public string? Path { get; set; }
    public string? KeysDir { get; set; }
    public bool Json { get; set; }
    public string? OutDir { get; set; }
    public string? Filter { get; set; }
    public string? RapFile { get; set; }
    public string? RapDirectory { get; set; }
    public string? KlicHex { get; set; }
    public string? Error { get; set; }
}

/// <summary>A tiny positional/flag parser for <c>&lt;pkg&gt; [--keys DIR] [--json]</c>.</summary>
internal static class CommandLine
{
    public static Options Parse(ReadOnlySpan<string> args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--json":
                    o.Json = true;
                    break;

                case "--keys":
                    if (i + 1 >= args.Length)
                    {
                        o.Error = "--keys requires a directory argument.";
                        return o;
                    }
                    o.KeysDir = args[++i];
                    break;

                case "--out":
                    if (i + 1 >= args.Length) { o.Error = "--out requires a directory argument."; return o; }
                    o.OutDir = args[++i];
                    break;

                case "--filter":
                    if (i + 1 >= args.Length) { o.Error = "--filter requires a glob pattern."; return o; }
                    o.Filter = args[++i];
                    break;

                case "--rap":
                    if (i + 1 >= args.Length) { o.Error = "--rap requires a RAP file path."; return o; }
                    o.RapFile = args[++i];
                    break;

                case "--rap-dir":
                    if (i + 1 >= args.Length) { o.Error = "--rap-dir requires a directory path."; return o; }
                    o.RapDirectory = args[++i];
                    break;

                case "--klic":
                case "--klicensee":
                    if (i + 1 >= args.Length) { o.Error = $"{a} requires a 32-hex-char klicensee."; return o; }
                    o.KlicHex = args[++i];
                    break;

                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        o.Error = $"unknown option '{a}'.";
                        return o;
                    }
                    if (o.Path is not null)
                    {
                        o.Error = $"unexpected extra argument '{a}'.";
                        return o;
                    }
                    o.Path = a;
                    break;
            }
        }
        return o;
    }
}

/// <summary>Simple glob (<c>*</c>, <c>?</c>). A pattern with '/' matches the full path, else the leaf name.</summary>
internal static class Glob
{
    public static Func<string, bool> Matcher(string pattern)
    {
        bool fullPath = pattern.Contains('/');
        var regex = new Regex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase);
        return name =>
        {
            string target = fullPath ? name
                : name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..]
                : name;
            return regex.IsMatch(target);
        };
    }
}

internal static class CliHelp
{
    public static void PrintUsage()
    {
        Console.WriteLine(FeatureText.CliSummary);
        Console.WriteLine();
        Console.WriteLine(
            """
            Usage:
              pkglens --version
              pkglens info    <pkg> [--keys DIR] [--json]   header + content-id + SFO summary
              pkglens list    <pkg> [--keys DIR] [--json]   entry table
              pkglens sfo     <pkg> [--keys DIR] [--json]   dump PARAM.SFO key/values
              pkglens verify  <pkg> [--keys DIR] [--json]   check header CMAC/SHA-1 + structure
              pkglens extract <pkg> [--out DIR] [--filter GLOB] [--keys DIR] [--json]   unpack files to a folder
              pkglens decrypt <edat> [--rap FILE | --klic HEX] [--out FILE] [--json]   decrypt an EDAT/SDAT file
              pkglens self    <eboot> [--json]               inspect a SELF/EBOOT.BIN header
              pkglens unself  <eboot> [--out FILE] [--rap FILE | --klic HEX]   decrypt a SELF → plaintext ELF
              pkglens resign  <elf> [--out FILE] [--npdrm]   ELF → fake-signed SELF (fSELF) for CFW
                       custom sign: [--auth-id HEX] [--vendor-id HEX] [--app-version HEX] [--type N] [--content-id CID]
                                    [--fw-version M.NN] [--control-flags 64-HEX]
                                    [--np-license-type FREE|LOCAL|NETWORK] [--np-app-type SPRX|EXEC|USPRX|UEXEC]
              pkglens patch   <eboot> [--sdk-version VER] [--find HEX --replace HEX] [--at OFF=HEX] [--resign]
                       magic-patch an EBOOT/ELF (e.g. lower the firmware requirement: --sdk-version 4.00)
              pkglens folderinfo <folder> [--json]           report on an extracted content folder
              pkglens scan    <dir> [--recursive] [--json | --csv] [--keys DIR]   catalog a folder of .pkg files
              pkglens audit   <pkg|file|folder> [--keys DIR] [--rap-dir DIR] [--json]
                       report package keys, SELF revisions, licenses, RAP availability, and unsupported encryption
              pkglens unpbp   <EBOOT.PBP> [--out DIR] [--list]   split a PSP PBP into its parts (SFO/icons/DATA.PSP/DATA.PSAR)
              pkglens undoc   <DOCUMENT.DAT> [--docinfo FILE] [--out DIR]   decrypt a PSP/minis manual to PNG pages
              pkglens psar    info|decrypt <DATA.PSAR> [--out FILE]   decrypt a PSP NPUMDIMG (minis) to .iso (keyless)
              pkglens pspexport <pkg> [--format pbp|iso|cso] [--out FILE] [--json]
                       export a PSP package directly; format defaults to output extension, then ISO
              pkglens vitaexport <pkg> [--out DIR] [--work-bin FILE] [--json]
                       classify and export a Vita app/update/DLC/theme to app/patch/addcont layout
              pkglens pack    <folder> [--out FILE] [options]   build a .pkg from a content folder
              pkglens keys    import|status|where           manage an optional override retail key
              pkglens raps    import|list|status|remove     manage stored RAP licenses

            Options:
              --keys DIR   directory holding an override NPDRM PKG PS3 AES key file
                           (also read from $PKGLENS_KEYS or ~/.pkglens). The standard retail
                           key is bundled, so this is only needed for a non-standard key.
              --out DIR    extract destination (default: a folder named after the package)
              --filter GLOB  only extract matching entries, e.g. "*.SFO" or "USRDIR/*"
              --json       machine-readable success output for every command
              --version    print the release-derived binary version
              --rap-dir DIR  RAP library override (also read from $PKGLENS_RAPS)
              $PKGLENS_KLICENSEES  local klicensee JSON database override

            pack options (Fast Pack infers these from PARAM.SFO; pass any to Custom Pack):
              --out FILE       output .pkg path (default: <content-id>.pkg)
              --content-id CID 36-char content id, e.g. UP0001-NPUB30910_00-EXAMPLE000000001
              --install-dir D  install directory metadata (default: TITLE_ID)
              --content-type T GameExec | GameData | Theme | … or a number
              --drm-type N     DRM type metadata (default: 3 = free)
              --debug          non-finalized package (RPCS3 / dev consoles; needs no key).
                               Default is retail-encrypted (needs the key) — the format a
                               jailbroken/CFW PS3 installs; unsigned, so stock retail won't take it.
              --resign         fake-sign EBOOT.BIN as it is packed (boots on CFW without a license);
                               licensed content auto-resolves from local key/RAP libraries; --rap/--klic override it.

            Exit codes: 0 ok · 1 usage · 2 parse error · 3 key/decryption error · 4 integrity failure
            """);
    }
}
