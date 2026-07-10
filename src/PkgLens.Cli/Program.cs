using PkgLens.Core;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;
using PkgLens.Cli;

// pkglens <command> <pkg> [--keys DIR] [--json]
//   info | list | sfo
//
// A deliberately small, dependency-free arg parser (the spec suggests System.CommandLine; that
// package's API churns across previews, so a hand-rolled parser keeps the build friction-free).

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
        Func<PkgLens.Core.Models.PkgEntry, bool>? filter = null;
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
        var info = PkgLens.Core.Self.SelfReader.ParseInfo(s);
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
        var npd = PkgLens.Core.Npd.EdatFile.ParseHeader(src);
        Console.WriteLine($"{npd.ContentId}  (v{npd.Version}, DRM {npd.LicenseText}{(npd.IsSdat ? ", SDAT" : "")})");

        byte[]? klic = null;
        if (npd.NeedsKlicensee)
        {
            if (parsed.RapFile is null)
            {
                Console.Error.WriteLine($"error: '{npd.ContentId}' is a licensed EDAT — supply its RAP with --rap FILE.");
                return ExitCode.KeyOrDecryptError;
            }
            klic = PkgLens.Core.Npd.NpdKeys.RapToKlicensee(File.ReadAllBytes(parsed.RapFile));
        }

        string outPath = parsed.OutDir ?? StripNpdExtension(parsed.Path);
        using (var dst = File.Create(outPath))
            PkgLens.Core.Npd.EdatFile.Decrypt(src, dst, klic);
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
