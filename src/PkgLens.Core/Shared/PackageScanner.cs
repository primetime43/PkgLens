using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

/// <summary>
/// One catalog row produced by scanning a package file. A file that could not be parsed becomes an
/// error row (<see cref="Platform"/> == "?", with the reason in <see cref="Note"/>) rather than
/// aborting the whole scan.
/// </summary>
public sealed record PackageScanRow(
    string File,
    string? ContentId,
    string Platform,
    string Finalization,
    string? TitleId,
    string? Title,
    string? Version,
    long Size,
    bool Decrypted,
    string? Note)
{
    /// <summary>True for a file that failed to parse (an error row).</summary>
    public bool Failed => Platform == "?";
}

/// <summary>
/// Inspects <c>.pkg</c> files for the batch catalog shared by the CLI <c>scan</c> command and the
/// GUI Scan dialog. Opening files is done here (Core's convenience I/O surface, like
/// <see cref="PkgReader.Open"/>); choosing which files to scan stays with the caller.
/// </summary>
public static class PackageScanner
{
    /// <summary>Inspects one package into a catalog row; parse/key/IO failures yield an error row.</summary>
    public static PackageScanRow Inspect(string path, IKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(keys);

        long size = 0;
        try { size = new FileInfo(path).Length; } catch { /* size stays 0 */ }

        try
        {
            PkgInfo info = PkgReader.Open(path, keys);
            var h = info.Header;
            return new PackageScanRow(
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
            return new PackageScanRow(path, null, "?", "?", null, null, null, size, false, ex.Message);
        }
    }

    /// <summary>Renders scan rows as RFC-4180 CSV (with a header row), for export.</summary>
    public static string ToCsv(IEnumerable<PackageScanRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new System.Text.StringBuilder();
        sb.Append("file,content_id,platform,finalization,title_id,title,version,size_bytes,decrypted,note\n");
        foreach (var r in rows)
            sb.Append(string.Join(',', new[]
            {
                Csv(r.File), Csv(r.ContentId), Csv(r.Platform), Csv(r.Finalization),
                Csv(r.TitleId), Csv(r.Title), Csv(r.Version), r.Size.ToString(),
                r.Decrypted ? "true" : "false", Csv(r.Note),
            })).Append('\n');
        return sb.ToString();
    }

    /// <summary>Renders scan rows as indented JSON, for export.</summary>
    public static string ToJson(IEnumerable<PackageScanRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return System.Text.Json.JsonSerializer.Serialize(
            rows.Select(r => new
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
            }),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Quotes a CSV field per RFC 4180 when it contains a comma, quote, or newline.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.AsSpan().IndexOfAny(",\"\n\r") < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
