using PkgLens.Core;
using PkgLens.Core.Keys;

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
            Console.Error.WriteLine($"No .pkg files found in {dir}{(recursive ? " (recursive)" : "")}.");
            return ExitCode.Ok;
        }

        IKeyProvider keys = new FileKeyProvider(keysDir);
        var rows = files.Select(f => PackageScanner.Inspect(f, keys)).ToList();

        switch (format)
        {
            case Format.Json: RenderJson(rows); break;
            case Format.Csv: RenderCsv(rows); break;
            default: RenderTable(rows); break;
        }
        return ExitCode.Ok;
    }

    // ---- renderers -------------------------------------------------------------------------------

    private static void RenderTable(IReadOnlyList<PackageScanRow> rows)
    {
        static string Cell(string? s) => string.IsNullOrEmpty(s) ? "-" : s;

        Console.WriteLine($"{"CONTENT-ID",-36}  {"PLAT",-6} {"FINAL",-6} {"DEC",-3} {"SIZE",15}  {"VER",-8}  TITLE");
        foreach (var r in rows)
        {
            string dec = r.Note is not null && r.Platform == "?" ? "err" : (r.Decrypted ? "ok" : "no");
            string title = r.Note is not null && r.Platform == "?" ? $"[{r.Note}]" : Cell(r.Title);
            Console.WriteLine(
                $"{Cell(r.ContentId),-36}  {r.Platform,-6} {r.Finalization,-6} {dec,-3} {r.Size,15:n0}  {Cell(r.Version),-8}  {title}");
        }

        int ok = rows.Count(r => r.Decrypted);
        int failed = rows.Count(r => r.Platform == "?");
        long total = rows.Sum(r => r.Size);
        Console.WriteLine();
        Console.WriteLine($"{rows.Count} package(s), {total:n0} bytes — {ok} decrypted" +
                          (failed > 0 ? $", {failed} unreadable" : "") + ".");
    }

    private static void RenderJson(IReadOnlyList<PackageScanRow> rows) =>
        Console.WriteLine(PackageScanner.ToJson(rows));

    private static void RenderCsv(IReadOnlyList<PackageScanRow> rows) =>
        Console.Write(PackageScanner.ToCsv(rows));
}
