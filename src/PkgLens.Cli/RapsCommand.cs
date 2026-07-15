using PkgLens.Core.Ps3.Npd;

namespace PkgLens.Cli;

/// <summary><c>pkglens raps</c> — manages the content-id-indexed RAP library.</summary>
internal static class RapsCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        string action = args[0].ToLowerInvariant();
        string? value = null, contentId = null, directory = null;
        bool json = false, force = false;

        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--json": json = true; break;
                case "--force": force = true; break;
                case "--content-id":
                    if (!Take(args, ref index, argument, out contentId)) return ExitCode.Usage;
                    break;
                case "--rap-dir":
                    if (!Take(args, ref index, argument, out directory)) return ExitCode.Usage;
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"error: unknown option '{argument}'.");
                        return ExitCode.Usage;
                    }
                    if (value is not null)
                    {
                        Console.Error.WriteLine($"error: unexpected extra argument '{argument}'.");
                        return ExitCode.Usage;
                    }
                    value = argument;
                    break;
            }
        }

        try
        {
            return action switch
            {
                "import" => Import(value, contentId, directory, force, json),
                "list" => List(value, contentId, directory, force, json),
                "remove" => Remove(value, contentId, directory, force, json),
                "status" => Status(value, contentId, directory, force, json),
                _ => Unknown(action),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCode.Usage;
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
    }

    private static int Import(string? source, string? contentId, string? directory, bool force, bool json)
    {
        if (source is null)
        {
            Console.Error.WriteLine("error: a RAP file is required.");
            Console.Error.WriteLine("usage: pkglens raps import <file.rap> [--content-id CID] [--force] [--rap-dir DIR]");
            return ExitCode.Usage;
        }
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"error: RAP file not found: {source}");
            return ExitCode.Usage;
        }

        contentId ??= Path.GetFileNameWithoutExtension(source);
        byte[] rap = File.ReadAllBytes(source);
        bool replaced = File.Exists(RapStore.PathFor(contentId, directory));
        string installed = RapStore.Install(contentId, rap, directory, overwrite: force);

        if (json)
            CliJson.Write(new
            {
                command = "raps.import",
                contentId,
                path = Path.GetFullPath(installed),
                replaced,
            });
        else
            Console.WriteLine($"Imported {contentId} → {Path.GetFullPath(installed)}");
        return ExitCode.Ok;
    }

    private static int List(string? value, string? contentId, string? directory, bool force, bool json)
    {
        if (value is not null || contentId is not null || force)
            return InvalidOptions("list");

        IReadOnlyList<RapStoreEntry> entries = RapStore.List(directory);
        if (json)
        {
            CliJson.Write(new
            {
                command = "raps.list",
                directory = RapStore.DirectoryPath(directory),
                count = entries.Count,
                validCount = entries.Count(entry => entry.IsValid),
                invalidCount = entries.Count(entry => !entry.IsValid),
                entries = entries.Select(entry => new
                {
                    entry.ContentId,
                    path = Path.GetFullPath(entry.Path),
                    entry.Size,
                    entry.IsValid,
                    entry.Error,
                }),
            });
        }
        else if (entries.Count == 0)
        {
            Console.WriteLine($"No RAPs installed in {RapStore.DirectoryPath(directory)}");
        }
        else
        {
            foreach (RapStoreEntry entry in entries)
                Console.WriteLine($"{entry.ContentId,-48} {(entry.IsValid ? "valid" : "INVALID")}  {entry.Size,3} bytes" +
                                  (entry.Error is null ? "" : $"  {entry.Error}"));
            Console.WriteLine($"{entries.Count} RAP(s): {entries.Count(entry => entry.IsValid)} valid, " +
                              $"{entries.Count(entry => !entry.IsValid)} invalid.");
        }
        return ExitCode.Ok;
    }

    private static int Remove(string? value, string? contentId, string? directory, bool force, bool json)
    {
        if (contentId is not null || force) return InvalidOptions("remove");
        if (value is null)
        {
            Console.Error.WriteLine("error: a content ID is required.");
            return ExitCode.Usage;
        }

        bool removed = RapStore.Remove(value, directory);
        if (!removed)
        {
            Console.Error.WriteLine($"error: no RAP is installed for '{value}'.");
            return ExitCode.KeyOrDecryptError;
        }

        if (json)
            CliJson.Write(new { command = "raps.remove", contentId = value, removed = true });
        else
            Console.WriteLine($"Removed RAP for {value}");
        return ExitCode.Ok;
    }

    private static int Status(string? value, string? contentId, string? directory, bool force, bool json)
    {
        if (contentId is not null || force) return InvalidOptions("status");
        string root = RapStore.DirectoryPath(directory);
        IReadOnlyList<RapStoreEntry> entries = RapStore.List(directory);

        if (value is not null)
        {
            RapStore.ValidateContentId(value);
            RapStoreEntry? entry = entries.FirstOrDefault(item =>
                item.ContentId.Equals(value, StringComparison.OrdinalIgnoreCase));
            bool installed = entry is not null;
            bool valid = entry?.IsValid == true;
            if (json)
                CliJson.Write(new
                {
                    command = "raps.status",
                    directory = root,
                    contentId = value,
                    installed,
                    valid,
                    path = entry?.Path,
                    error = entry?.Error,
                });
            else
                Console.WriteLine(installed
                    ? $"{value}: {(valid ? "installed and valid" : $"installed but invalid — {entry!.Error}")} ({entry!.Path})"
                    : $"{value}: not installed ({RapStore.PathFor(value, directory)})");
            return valid ? ExitCode.Ok : ExitCode.KeyOrDecryptError;
        }

        int validCount = entries.Count(entry => entry.IsValid);
        int invalidCount = entries.Count - validCount;
        if (json)
            CliJson.Write(new
            {
                command = "raps.status",
                directory = root,
                exists = Directory.Exists(root),
                count = entries.Count,
                validCount,
                invalidCount,
            });
        else
        {
            Console.WriteLine($"RAP library : {root}");
            Console.WriteLine($"Directory   : {(Directory.Exists(root) ? "exists" : "not created")}");
            Console.WriteLine($"Entries     : {entries.Count} ({validCount} valid, {invalidCount} invalid)");
        }
        return invalidCount == 0 ? ExitCode.Ok : ExitCode.KeyOrDecryptError;
    }

    private static int Unknown(string action)
    {
        Console.Error.WriteLine($"error: unknown raps command '{action}'.");
        PrintUsage();
        return ExitCode.Usage;
    }

    private static int InvalidOptions(string action)
    {
        Console.Error.WriteLine($"error: invalid option or argument for 'raps {action}'.");
        return ExitCode.Usage;
    }

    private static bool Take(ReadOnlySpan<string> args, ref int index, string option, out string value)
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"error: {option} requires an argument.");
            value = string.Empty;
            return false;
        }
        value = args[++index];
        return true;
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        RAP library:
          pkglens raps import <file.rap> [--content-id CID] [--force] [--rap-dir DIR] [--json]
          pkglens raps list [--rap-dir DIR] [--json]
          pkglens raps status [CONTENT-ID] [--rap-dir DIR] [--json]
          pkglens raps remove <CONTENT-ID> [--rap-dir DIR] [--json]
        """);
}
