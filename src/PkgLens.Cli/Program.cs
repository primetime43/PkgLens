using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Cli;

// UTF-8 output so ✓ / · / ⚠ render instead of mojibake; ignore when the console is redirected/absent.
try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
catch { /* no console or output redirected */ }

// One top-level handler so every subcommand maps failures to a documented exit code (never a raw
// stack trace). Command-specific catches still run first; this catches whatever escapes them.
try
{

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    CliHelp.PrintUsage();
    return args.Length == 0 ? ExitCode.Usage : ExitCode.Ok;
}

string command = args[0].ToLowerInvariant();

// The `keys` command group manages the runtime key and takes no <pkg> argument.
if (command == "keys")
    return KeysCommand.Run(args.AsSpan(1));

// The `pack` command builds a package from a <folder> and has its own option set.
if (command == "pack")
    return PackCommand.Run(args.AsSpan(1));

// The `resign` command builds a fSELF from an <elf> and has its own option set.
if (command == "resign")
    return ResignCommand.Run(args.AsSpan(1));

// The `unself` command decrypts a SELF/EBOOT.BIN back to a plaintext ELF.
if (command == "unself")
    return UnselfCommand.Run(args.AsSpan(1));

// The `folderinfo` command reports on a content <folder>, not a <pkg>.
if (command == "folderinfo")
    return FolderInfoCommand.Run(args.AsSpan(1));

// The `scan` command catalogs a <dir> of .pkg files, not a single <pkg>.
if (command == "scan")
    return ScanCommand.Run(args.AsSpan(1));

// The `unpbp` command splits a PSP PBP container (EBOOT.PBP) into its parts.
if (command == "unpbp")
    return UnpbpCommand.Run(args.AsSpan(1));

// The `undoc` command decrypts a PSP DOCUMENT.DAT manual into PNG pages.
if (command == "undoc")
    return UndocCommand.Run(args.AsSpan(1));

// The `psar` command inspects/decrypts a PSP NPUMDIMG image (DATA.PSAR) to an ISO.
if (command == "psar")
    return PsarCommand.Run(args.AsSpan(1));

// The `patch` command applies magic patches to an EBOOT/ELF.
if (command == "patch")
    return PatchCommand.Run(args.AsSpan(1));

var parsed = CommandLine.Parse(args.AsSpan(1));

if (parsed.Error is not null)
{
    Console.Error.WriteLine($"error: {parsed.Error}");
    CliHelp.PrintUsage();
    return ExitCode.Usage;
}

if (parsed.Path is null)
{
    Console.Error.WriteLine("error: a <pkg> file path is required.");
    CliHelp.PrintUsage();
    return ExitCode.Usage;
}

if (!File.Exists(parsed.Path))
{
    Console.Error.WriteLine($"error: file not found: {parsed.Path}");
    return ExitCode.Usage;
}

IKeyProvider keys = new FileKeyProvider(parsed.KeysDir);

if (command == "verify")
{
    try
    {
        using var stream = File.OpenRead(parsed.Path);
        var report = PkgVerifier.Verify(stream, keys);
        Render.Verify(report, parsed);
        return report.Failures > 0 ? ExitCode.IntegrityFailure : ExitCode.Ok;
    }
    catch (PkgKeyException ex) { Console.Error.WriteLine($"key error: {ex.Message}"); return ExitCode.KeyOrDecryptError; }
    catch (PkgFormatException ex) { Console.Error.WriteLine($"parse error: {ex.Message}"); return ExitCode.ParseError; }
}

if (command == "extract")
{
    try
    {
        using var stream = File.OpenRead(parsed.Path);
        var info = PkgReader.Read(stream, keys);
        if (!info.IsDecrypted)
        {
            Console.Error.WriteLine($"decryption unavailable: {info.DecryptionNote ?? "a key is required."}");
            return ExitCode.KeyOrDecryptError;
        }

        string outDir = parsed.OutDir ?? Path.GetFileNameWithoutExtension(parsed.Path);
        Func<PkgLens.Core.Shared.Models.PkgEntry, bool>? filter = null;
        if (parsed.Filter is not null)
        {
            var match = Glob.Matcher(parsed.Filter);
            filter = e => match(e.Name);
        }

        int n = PkgReader.ExtractAll(stream, info, outDir, keys, filter,
            e => Console.WriteLine($"  {e.Name}"));
        Console.WriteLine($"Extracted {n} file(s) to {Path.GetFullPath(outDir)}");
        return ExitCode.Ok;
    }
    catch (PkgKeyException ex) { Console.Error.WriteLine($"key error: {ex.Message}"); return ExitCode.KeyOrDecryptError; }
    catch (PkgFormatException ex) { Console.Error.WriteLine($"parse error: {ex.Message}"); return ExitCode.ParseError; }
}

if (command == "self")
{
    try
    {
        using var s = File.OpenRead(parsed.Path);
        var info = PkgLens.Core.Ps3.Self.SelfReader.ParseInfo(s);
        Render.Self(info);
        return ExitCode.Ok;
    }
    catch (PkgFormatException ex) { Console.Error.WriteLine($"parse error: {ex.Message}"); return ExitCode.ParseError; }
}

if (command == "decrypt")
{
    try
    {
        using var src = File.OpenRead(parsed.Path);

        // PSP EDAT ("\0PSPEDAT") and bare PGD ("\0PGD") decrypt via the PSP AMCTRL/PGD path (no RAP).
        if (PkgLens.Core.Psp.PspEdatFile.IsPspEncrypted(src))
        {
            byte[] allBytes = File.ReadAllBytes(parsed.Path);
            if (PkgLens.Core.Psp.PspEdatFile.IsPspEdat(allBytes))
            {
                var pspInfo = PkgLens.Core.Psp.PspEdatFile.ParseHeader(allBytes);
                Console.WriteLine($"{pspInfo.ContentId}  (PSP EDAT, DRM {pspInfo.DrmType})");
            }
            else
            {
                Console.WriteLine("(bare PSP PGD)");
            }
            string pspOut = parsed.OutDir ?? StripNpdExtension(parsed.Path);
            using (var dst = File.Create(pspOut))
                PkgLens.Core.Psp.PspEdatFile.Decrypt(src, dst);
            Console.WriteLine($"Decrypted → {pspOut}");
            return ExitCode.Ok;
        }

        var npd = PkgLens.Core.Ps3.Npd.EdatFile.ParseHeader(src);
        Console.WriteLine($"{npd.ContentId}  (v{npd.Version}, DRM {npd.LicenseText}{(npd.IsSdat ? ", SDAT" : "")})");

        byte[]? klic = null;
        if (npd.NeedsKlicensee)
        {
            try
            {
                if (parsed.KlicHex is not null) klic = NpKlic.ParseHex(parsed.KlicHex);
                else if (parsed.RapFile is not null) klic = NpKlic.FromRapFile(parsed.RapFile);
            }
            catch (FormatException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return ExitCode.Usage; }

            if (klic is null)
            {
                Console.Error.WriteLine($"error: '{npd.ContentId}' is a licensed EDAT — supply its RAP with --rap FILE, or the klicensee with --klic HEX.");
                return ExitCode.KeyOrDecryptError;
            }
        }

        string outPath = parsed.OutDir ?? StripNpdExtension(parsed.Path);
        using (var dst = File.Create(outPath))
            PkgLens.Core.Ps3.Npd.EdatFile.Decrypt(src, dst, klic);
        Console.WriteLine($"Decrypted {npd.FileSize:n0} bytes → {outPath}");
        return ExitCode.Ok;
    }
    catch (PkgKeyException ex) { Console.Error.WriteLine($"key error: {ex.Message}"); return ExitCode.KeyOrDecryptError; }
    catch (PkgFormatException ex) { Console.Error.WriteLine($"parse error: {ex.Message}"); return ExitCode.ParseError; }
}

try
{
    PkgInfo info = PkgReader.Open(parsed.Path, keys);

    switch (command)
    {
        case "info":
            Render.Info(info, parsed);
            return ExitCode.Ok;

        case "list":
            if (!RequireDecryption(info)) return ExitCode.KeyOrDecryptError;
            Render.List(info, parsed);
            return ExitCode.Ok;

        case "sfo":
            if (!RequireDecryption(info)) return ExitCode.KeyOrDecryptError;
            if (info.Sfo is null)
            {
                Console.Error.WriteLine("error: no PARAM.SFO found in this package.");
                return ExitCode.ParseError;
            }
            Render.Sfo(info, parsed);
            return ExitCode.Ok;

        default:
            Console.Error.WriteLine($"error: unknown command '{command}'.");
            CliHelp.PrintUsage();
            return ExitCode.Usage;
    }
}
catch (PkgKeyException ex)
{
    Console.Error.WriteLine($"key error: {ex.Message}");
    return ExitCode.KeyOrDecryptError;
}
catch (PkgFormatException ex)
{
    Console.Error.WriteLine($"parse error: {ex.Message}");
    return ExitCode.ParseError;
}

}
// Domain exceptions map to their documented codes even if a subcommand let one escape.
catch (PkgKeyException ex)
{
    Console.Error.WriteLine($"key error: {ex.Message}");
    return ExitCode.KeyOrDecryptError;
}
catch (PkgFormatException ex)
{
    Console.Error.WriteLine($"parse error: {ex.Message}");
    return ExitCode.ParseError;
}
catch (IOException ex)
{
    Console.Error.WriteLine($"i/o error: {ex.Message}");
    return ExitCode.Usage;
}
catch (UnauthorizedAccessException ex)
{
    Console.Error.WriteLine($"access error: {ex.Message}");
    return ExitCode.Usage;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return ExitCode.Usage;
}

static string StripNpdExtension(string path)
{
    string ext = Path.GetExtension(path);
    if (ext.Equals(".edat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".sdat", StringComparison.OrdinalIgnoreCase))
        return path[..^ext.Length];
    return path + ".dec";
}

static bool RequireDecryption(PkgInfo info)
{
    if (info.IsDecrypted) return true;
    Console.Error.WriteLine(
        $"decryption unavailable: {info.DecryptionNote ?? "no decryptor could be resolved."}");
    return false;
}
