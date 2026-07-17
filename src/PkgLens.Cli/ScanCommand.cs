using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens scan &lt;dir&gt; [--recursive] [--json|--csv] [--keys DIR]</c> — walk a folder of
/// <c>.pkg</c> files and emit one catalog row each (content id, title, title id, version, size,
/// retail/debug, decrypt-ok). A package that fails to parse becomes a row with an error note rather
/// than aborting the whole scan.
/// </summary>
internal static class ScanCommand
{
    private enum Format { Table, Json, Csv }

    public static int Run(ReadOnlySpan<string> args)
    {
        string? dir = null, keysDir = null;
        bool recursive = false;
        var format = Format.Table;
        bool jsonSeen = false, csvSeen = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--json": format = Format.Json; jsonSeen = true; break;
                case "--csv": format = Format.Csv; csvSeen = true; break;
                case "--recursive":
                case "-r": recursive = true; break;
                case "--keys":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --keys requires a directory argument."); return ExitCode.Usage; }
                    keysDir = args[++i];
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"error: unknown option '{a}'."); return ExitCode.Usage; }
                    if (dir is not null) { Console.Error.WriteLine($"error: unexpected extra argument '{a}'."); return ExitCode.Usage; }
                    dir = a;
                    break;
            }
        }

        if (jsonSeen && csvSeen)
        {
            Console.Error.WriteLine("error: choose either --json or --csv, not both.");
            return ExitCode.Usage;
        }
        if (dir is null)
        {
            Console.Error.WriteLine("error: a <dir> to scan is required.");
            Console.Error.WriteLine("usage: pkglens scan <dir> [--recursive] [--json | --csv] [--keys DIR]");
            return ExitCode.Usage;
        }
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"error: directory not found: {dir}");
            return ExitCode.Usage;
        }

        var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(dir, "*", search)
            .Where(f => Path.GetExtension(f).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            var empty = PackageLibraryMatcher.Analyze(Array.Empty<PackageScanRow>());
            if (format == Format.Json)
                Console.WriteLine(PackageLibraryMatcher.ToJson(empty));
            else if (format == Format.Csv)
                Console.Write(PackageLibraryMatcher.ToCsv(empty));
            else
                Console.Error.WriteLine($"No .pkg files found in {dir}{(recursive ? " (recursive)" : "")}.");
            return ExitCode.Ok;
        }

        IKeyProvider keys = new FileKeyProvider(keysDir);
        var rows = files.Select(f => PackageScanner.Inspect(f, keys)).ToList();
        PackageLibraryReport report = PackageLibraryMatcher.Analyze(rows);

        switch (format)
        {
            case Format.Json: Console.WriteLine(PackageLibraryMatcher.ToJson(report)); break;
            case Format.Csv: Console.Write(PackageLibraryMatcher.ToCsv(report)); break;
            default: RenderTable(report); break;
        }
        return ExitCode.Ok;
    }

    // ---- renderers -------------------------------------------------------------------------------

    private static void RenderTable(PackageLibraryReport report)
    {
        static string Cell(string? s) => string.IsNullOrEmpty(s) ? "-" : s;

        Console.WriteLine($"{"TITLE-ID",-12} {"REGION",-7} {"ROLE",-7} {"CAT",-4} {"VER",-8} {"SIZE",13}  TITLE / FILE");
        foreach (PackageScanRow row in report.Packages)
        {
            string title = row.Failed ? $"[{row.Note}]" : $"{Cell(row.Title)} / {Path.GetFileName(row.File)}";
            Console.WriteLine(
                $"{Cell(row.TitleId),-12} {Cell(row.Region),-7} {row.Role,-7} {Cell(row.Category),-4} " +
                $"{Cell(row.Version),-8} {row.Size,13:n0}  {title}");
        }

        int ok = report.Packages.Count(row => row.Decrypted);
        int failed = report.Packages.Count(row => row.Failed);
        long total = report.Packages.Sum(row => row.Size);
        Console.WriteLine();
        Console.WriteLine($"{report.Packages.Count} package(s) in {report.Groups.Count} title group(s), {total:n0} bytes — " +
                          $"{report.Warnings.Count} warning(s), {ok} decrypted" +
                           (failed > 0 ? $", {failed} unreadable" : "") + ".");
        foreach (PackageLibraryWarning warning in report.Warnings)
            Console.WriteLine($"warning: {warning.Message}");
    }
}
