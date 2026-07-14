using System.Text;
using System.Text.Json;
using PkgLens.Core;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;

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

    private sealed record ScanRow(
        string File,
        string? ContentId,
        string Platform,
        string Finalization,
        string? TitleId,
        string? Title,
        string? Version,
        long Size,
        bool Decrypted,
        string? Note);

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
        var rows = files.Select(f => Inspect(f, keys)).ToList();

        switch (format)
        {
            case Format.Json: RenderJson(rows); break;
            case Format.Csv: RenderCsv(rows); break;
            default: RenderTable(rows); break;
        }
        return ExitCode.Ok;
    }

    private static ScanRow Inspect(string path, IKeyProvider keys)
    {
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { /* size stays 0 */ }

        try
        {
            PkgInfo info = PkgReader.Open(path, keys);
            var h = info.Header;
            return new ScanRow(
                File: path,
                ContentId: info.ContentId.Raw is { Length: > 0 } cid ? cid : null,
                Platform: h.PlatformDisplay,
                Finalization: h.Finalization.ToString(),
                TitleId: info.Sfo?.TitleId ?? info.ContentId.TitleId,
                Title: info.Sfo?.Title ?? info.ContentId.Name,
                Version: info.Sfo?.AppVersion ?? info.Sfo?.Version,
                Size: size,
                Decrypted: info.IsDecrypted,
                Note: info.IsDecrypted ? null : info.DecryptionNote);
        }
        catch (Exception ex) when (ex is PkgFormatException or PkgKeyException or IOException or UnauthorizedAccessException)
        {
            return new ScanRow(path, null, "?", "?", null, null, null, size, false, ex.Message);
        }
    }

    // ---- renderers -------------------------------------------------------------------------------

    private static void RenderTable(IReadOnlyList<ScanRow> rows)
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

    private static void RenderJson(IReadOnlyList<ScanRow> rows)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        Console.WriteLine(JsonSerializer.Serialize(rows.Select(r => new
        {
            file = r.File,
            contentId = r.ContentId,
            platform = r.Platform,
            finalization = r.Finalization,
            titleId = r.TitleId,
            title = r.Title,
            version = r.Version,
            size = r.Size,
            decrypted = r.Decrypted,
            note = r.Note,
        }), options));
    }

    private static void RenderCsv(IReadOnlyList<ScanRow> rows)
    {
        Console.WriteLine("file,content_id,platform,finalization,title_id,title,version,size_bytes,decrypted,note");
        foreach (var r in rows)
        {
            Console.WriteLine(string.Join(',', new[]
            {
                Csv(r.File), Csv(r.ContentId), Csv(r.Platform), Csv(r.Finalization),
                Csv(r.TitleId), Csv(r.Title), Csv(r.Version), r.Size.ToString(),
                r.Decrypted ? "true" : "false", Csv(r.Note),
            }));
        }
    }

    /// <summary>Quotes a CSV field per RFC 4180 when it contains a comma, quote, or newline.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool needsQuote = value.AsSpan().IndexOfAny(",\"\n\r") >= 0;
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
