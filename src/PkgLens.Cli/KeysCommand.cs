using PkgLens.Core.Shared.Keys;

namespace PkgLens.Cli;

/// <summary>
/// The `pkglens keys` command group. The standard NPDRM PKG PS3 AES key is bundled, so retail
/// packages decrypt with no setup; these commands install/inspect an optional <em>override</em> key
/// file (e.g. an IDU/kiosk key) for the rare package the bundled key does not cover.
/// </summary>
internal static class KeysCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return ExitCode.Usage;
        }

        string sub = args[0].ToLowerInvariant();
        var rest = CommandLine.Parse(args[1..]);
        if (rest.Error is not null)
        {
            Console.Error.WriteLine($"error: {rest.Error}");
            return ExitCode.Usage;
        }

        return sub switch
        {
            "import" => Import(rest),
            "status" => Status(rest),
            "where" => Where(),
            _ => Unknown(sub),
        };
    }

    private static int Import(Options o)
    {
        if (o.Path is null)
        {
            Console.Error.WriteLine("error: `keys import` needs the key — 32 hex chars or a path to a key file.");
            Console.Error.WriteLine("       e.g. pkglens keys import 0123...cdef");
            return ExitCode.Usage;
        }

        byte[] key;
        try
        {
            key = KeyStore.ResolveKeyArgument(o.Path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not read the key: {ex.Message}");
            return ExitCode.KeyOrDecryptError;
        }

        string path = KeyStore.Install(key, o.KeysDir);
        Console.WriteLine($"Installed override key → {path}");
        Console.WriteLine($"  {(KeyStore.IsKnownGpkgKey(key) ? "✓" : "⚠")} {KeyStore.Describe(key)}");
        Console.WriteLine("This file overrides the bundled key for retail packages.");
        return ExitCode.Ok;
    }

    private static int Status(Options o)
    {
        var provider = new FileKeyProvider(o.KeysDir);

        Console.WriteLine("Bundled: standard NPDRM PKG PS3 AES key (retail packages decrypt out of the box).");
        Console.WriteLine();
        Console.WriteLine("Override search order (first match wins):");
        foreach (string dir in provider.SearchDirectories())
            Console.WriteLine($"  {dir}");
        Console.WriteLine($"Recognized filenames: {string.Join(", ", FileKeyProvider.KeyFileNames)}");
        Console.WriteLine();

        if (provider.TryLocateKeyFile(out string found))
        {
            try
            {
                byte[] key = KeyStore.ResolveKeyArgument(found);
                Console.WriteLine($"Override key in use: {found}");
                Console.WriteLine($"  {(KeyStore.IsKnownGpkgKey(key) ? "✓" : "⚠")} {KeyStore.Describe(key)}");
                return ExitCode.Ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Found override key file {found}, but it is invalid: {ex.Message}");
                return ExitCode.KeyOrDecryptError;
            }
        }

        Console.WriteLine("No override installed — using the bundled key.");
        Console.WriteLine($"Add an override with:  pkglens keys import <32-hex-key>   (writes to {KeyStore.DefaultDirectory})");
        return ExitCode.Ok;
    }

    private static int Where()
    {
        Console.WriteLine(System.IO.Path.Combine(KeyStore.DefaultDirectory, KeyStore.PrimaryFileName));
        return ExitCode.Ok;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"error: unknown keys subcommand '{sub}'.");
        PrintHelp();
        return ExitCode.Usage;
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            pkglens keys — manage the optional override NPDRM PKG PS3 AES key

            The standard retail key is bundled; retail packages decrypt with no setup. These commands
            only matter if you need to override it (e.g. an IDU/kiosk key).

            Usage:
              pkglens keys import <hex|file> [--keys DIR]   install an override key (default ~/.pkglens)
              pkglens keys status            [--keys DIR]   show the bundled key + any override
              pkglens keys where                            print the default override file path
            """);
}
