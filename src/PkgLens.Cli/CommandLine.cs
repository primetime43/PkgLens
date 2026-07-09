using System.Text.RegularExpressions;

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
        Console.WriteLine(
            """
            pkglens — inspect PS3 .pkg packages

            Usage:
              pkglens info    <pkg> [--keys DIR] [--json]   header + content-id + SFO summary
              pkglens list    <pkg> [--keys DIR] [--json]   entry table
              pkglens sfo     <pkg> [--keys DIR] [--json]   dump PARAM.SFO key/values
              pkglens verify  <pkg> [--keys DIR] [--json]   check header CMAC/SHA-1 + structure
              pkglens extract <pkg> [--out DIR] [--filter GLOB] [--keys DIR]   unpack files to a folder
              pkglens decrypt <edat> [--rap FILE] [--out FILE]   decrypt an EDAT/SDAT file
              pkglens keys    import|status|where           manage the runtime retail key

            Options:
              --keys DIR   directory holding the runtime NPDRM PKG PS3 AES key file
                           (also read from $PKGLENS_KEYS or ~/.pkglens). Retail packages
                           need this; debug packages decrypt without any key.
              --out DIR    extract destination (default: a folder named after the package)
              --filter GLOB  only extract matching entries, e.g. "*.SFO" or "USRDIR/*"
              --json       machine-readable output

            Exit codes: 0 ok · 1 usage · 2 parse error · 3 key/decryption error · 4 integrity failure
            """);
    }
}
