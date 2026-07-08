using PkgLens.Core.Keys;

namespace PkgLens.Cli;

/// <summary>
/// The `pkglens keys` command group: install and inspect the runtime PS3 gpkg AES key. The key is
/// never bundled — this just makes supplying your own copy a one-time, friendly step.
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
        Console.WriteLine($"Installed key → {path}");
        Console.WriteLine($"  {(KeyStore.IsKnownGpkgKey(key) ? "✓" : "⚠")} {KeyStore.Describe(key)}");
        Console.WriteLine("Retail packages will now decrypt automatically (no --keys needed).");
        return ExitCode.Ok;
    }

    private static int Status(Options o)
    {
        var provider = new FileKeyProvider(o.KeysDir);

        Console.WriteLine("Key search order (first match wins):");
        foreach (string dir in provider.SearchDirectories())
            Console.WriteLine($"  {dir}");
        Console.WriteLine($"Recognized filenames: {string.Join(", ", FileKeyProvider.KeyFileNames)}");
        Console.WriteLine();

        if (provider.TryLocateKeyFile(out string found))
        {
            try
            {
                byte[] key = KeyStore.ResolveKeyArgument(found);
                Console.WriteLine($"Found key: {found}");
                Console.WriteLine($"  {(KeyStore.IsKnownGpkgKey(key) ? "✓" : "⚠")} {KeyStore.Describe(key)}");
                return ExitCode.Ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Found key file {found}, but it is invalid: {ex.Message}");
                return ExitCode.KeyOrDecryptError;
            }
        }

        Console.WriteLine("No key installed yet. Debug packages still work without one.");
        Console.WriteLine($"Install with:  pkglens keys import <32-hex-key>   (writes to {KeyStore.DefaultDirectory})");
        Console.WriteLine("See docs/keys.md for where to obtain the standard NPDRM PKG PS3 AES key.");
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
            pkglens keys — manage the runtime NPDRM PKG PS3 AES key (never bundled)

            Usage:
              pkglens keys import <hex|file> [--keys DIR]   install your key (default ~/.pkglens)
              pkglens keys status            [--keys DIR]   show search path + whether a key is present
              pkglens keys where                            print the default key file path

            The standard NPDRM PKG PS3 AES key is public but not shipped here. See docs/keys.md
            for where to find it; `import` confirms whether the key you supply is the expected one.
            """);
}
