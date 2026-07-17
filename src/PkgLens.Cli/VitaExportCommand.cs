using PkgLens.Core.Vita;
using PkgLens.Core.Shared.Keys;

namespace PkgLens.Cli;

internal static class VitaExportCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? input = null;
        string? output = null;
        string? workBinPath = null;
        string? keysDirectory = null;
        bool json = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--out": if (!Take(args, ref index, argument, out output)) return ExitCode.Usage; break;
                case "--work-bin":
                case "--rif": if (!Take(args, ref index, argument, out workBinPath)) return ExitCode.Usage; break;
                case "--keys": if (!Take(args, ref index, argument, out keysDirectory)) return ExitCode.Usage; break;
                case "--json": json = true; break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"error: unknown option '{argument}'.");
                        return ExitCode.Usage;
                    }
                    if (input is not null)
                    {
                        Console.Error.WriteLine($"error: unexpected extra argument '{argument}'.");
                        return ExitCode.Usage;
                    }
                    input = argument;
                    break;
            }
        }

        if (input is null)
        {
            Console.Error.WriteLine("error: a PSVita <pkg> file is required.");
            Console.Error.WriteLine("usage: pkglens vitaexport <pkg> [--out DIR] [--work-bin FILE] [--keys DIR] [--json]");
            return ExitCode.Usage;
        }
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"error: file not found: {input}");
            return ExitCode.Usage;
        }
        if (workBinPath is not null && !File.Exists(workBinPath))
        {
            Console.Error.WriteLine($"error: license file not found: {workBinPath}");
            return ExitCode.Usage;
        }

        string destination = output ?? Path.GetFileNameWithoutExtension(input) + "_vita";
        byte[]? workBin = workBinPath is null ? null : File.ReadAllBytes(workBinPath);
        using var package = File.OpenRead(input);
        VitaExportResult result = VitaPackageExporter.Export(package, destination,
            new FileKeyProvider(keysDirectory), workBin);

        if (json)
        {
            CliJson.Write(new
            {
                command = "vitaexport",
                input = Path.GetFullPath(input),
                outputDirectory = result.OutputRoot,
                kind = result.Package.Kind,
                result.Package.Title,
                result.Package.TitleId,
                result.Package.ContentId,
                result.Package.Category,
                result.Package.AppVersion,
                result.Package.MinimumFirmware,
                result.Package.KeyRevision,
                result.Package.DrmType,
                result.FileCount,
                result.ExtractedBytes,
                result.LicenseStatus,
                result.Warnings,
            });
        }
        else
        {
            Console.WriteLine($"Exported Vita {result.Package.Kind} → {result.OutputRoot}");
            Console.WriteLine($"  title: {result.Package.Title} ({result.Package.TitleId})");
            Console.WriteLine($"  key revision: {result.Package.KeyRevision} · license: {result.LicenseStatus}");
            foreach (string warning in result.Warnings) Console.WriteLine($"  warning: {warning}");
        }
        return ExitCode.Ok;
    }

    private static bool Take(ReadOnlySpan<string> args, ref int index, string option, out string? value)
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"error: {option} requires a value.");
            value = null;
            return false;
        }
        value = args[++index];
        return true;
    }
}
