using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Keys;

namespace PkgLens.Cli;

internal static class PspExportCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? input = null;
        string? output = null;
        string? keysDirectory = null;
        PspExportFormat? format = null;
        bool json = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--format":
                    if (!Take(args, ref index, argument, out string? value)) return ExitCode.Usage;
                    if (!TryParseFormat(value!, out PspExportFormat parsed))
                    {
                        Console.Error.WriteLine($"error: unknown PSP export format '{value}' (use pbp, iso, or cso).");
                        return ExitCode.Usage;
                    }
                    format = parsed;
                    break;
                case "--out":
                    if (!Take(args, ref index, argument, out output)) return ExitCode.Usage;
                    break;
                case "--keys":
                    if (!Take(args, ref index, argument, out keysDirectory)) return ExitCode.Usage;
                    break;
                case "--json":
                    json = true;
                    break;
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
            Console.Error.WriteLine("error: a PSP <pkg> file is required.");
            Console.Error.WriteLine("usage: pkglens pspexport <pkg> [--format pbp|iso|cso] [--out FILE] [--keys DIR] [--json]");
            return ExitCode.Usage;
        }
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"error: file not found: {input}");
            return ExitCode.Usage;
        }

        format ??= InferFormat(output) ?? PspExportFormat.Iso;
        string destination = output ?? DefaultOutput(input, format.Value);
        AtomicOutput.EnsureDifferentPath(input, destination);

        using var package = File.OpenRead(input);
        var keys = new FileKeyProvider(keysDirectory);
        PspExportResult? result = null;
        AtomicOutput.Write(destination, stream =>
            result = PspPackageExporter.Export(package, stream, keys, format.Value));

        string fullOutput = Path.GetFullPath(destination);
        if (json)
        {
            CliJson.Write(new
            {
                command = "pspexport",
                input = Path.GetFullPath(input),
                output = fullOutput,
                format = result!.Format.ToString().ToLowerInvariant(),
                result.ContentId,
                result.PackageEntry,
                result.DiscId,
                result.IsoSize,
                result.OutputSize,
            });
        }
        else
        {
            Console.WriteLine($"Exported PSP package → {fullOutput} ({result!.OutputSize:n0} bytes, {result.Format})");
            if (result.DiscId is { Length: > 0 }) Console.WriteLine($"Disc ID: {result.DiscId}");
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

    private static bool TryParseFormat(string value, out PspExportFormat format)
    {
        format = value.ToLowerInvariant() switch
        {
            "pbp" => PspExportFormat.Pbp,
            "iso" => PspExportFormat.Iso,
            "cso" => PspExportFormat.Cso,
            _ => (PspExportFormat)(-1),
        };
        return Enum.IsDefined(format);
    }

    private static PspExportFormat? InferFormat(string? output) =>
        Path.GetExtension(output ?? string.Empty).ToLowerInvariant() switch
        {
            ".pbp" => PspExportFormat.Pbp,
            ".iso" => PspExportFormat.Iso,
            ".cso" => PspExportFormat.Cso,
            _ => null,
        };

    private static string DefaultOutput(string input, PspExportFormat format)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(input))!;
        return format == PspExportFormat.Pbp
            ? Path.Combine(directory, "EBOOT.PBP")
            : Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + "." + format.ToString().ToLowerInvariant());
    }
}
