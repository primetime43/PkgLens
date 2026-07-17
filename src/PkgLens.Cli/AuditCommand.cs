using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;

namespace PkgLens.Cli;

internal static class AuditCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? path = null;
        string? keysDirectory = null;
        string? rapDirectory = null;
        bool json = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--json":
                    json = true;
                    break;
                case "--keys":
                    if (!TryTake(args, ref index, out keysDirectory))
                        return UsageError("--keys requires a directory argument.");
                    break;
                case "--rap-dir":
                    if (!TryTake(args, ref index, out rapDirectory))
                        return UsageError("--rap-dir requires a directory argument.");
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                        return UsageError($"unknown option '{argument}'.");
                    if (path is not null)
                        return UsageError($"unexpected extra argument '{argument}'.");
                    path = argument;
                    break;
            }
        }

        if (path is null)
            return UsageError("audit requires a package, protected-content file, or folder path.");
        if (!File.Exists(path) && !Directory.Exists(path))
            return UsageError($"audit source not found: {path}");

        var report = KeyLicenseAudit.Inspect(path, new FileKeyProvider(keysDirectory),
            new KeyLicenseAuditOptions { RapDirectory = rapDirectory });
        Console.WriteLine(json ? KeyLicenseAudit.ToJson(report) : KeyLicenseAudit.ToText(report));
        return report.ErrorCount > 0 ? ExitCode.ParseError : ExitCode.Ok;
    }

    private static bool TryTake(ReadOnlySpan<string> args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            return false;
        }
        value = args[++index];
        return true;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine("usage: pkglens audit <pkg|file|folder> [--keys DIR] [--rap-dir DIR] [--json]");
        return ExitCode.Usage;
    }
}
