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

static bool RequireDecryption(PkgInfo info)
{
    if (info.IsDecrypted) return true;
    Console.Error.WriteLine(
        $"decryption unavailable: {info.DecryptionNote ?? "no decryptor could be resolved."}");
    return false;
}
