using System;
using System.IO;
using System.Linq;
using PkgLens.Core;
using PkgLens.Core.Shared;

namespace PkgLens.Gui.ViewModels;

/// <summary>Display wrapper over a <see cref="PackageScanRow"/> for the Scan dialog's grid.</summary>
public sealed class ScanResultRow
{
    public required PackageScanRow Row { get; init; }
    public string Match { get; init; } = string.Empty;

    public string Title => Row.Failed ? "(unreadable)" : (Row.Title ?? Row.ContentId ?? "(unknown)");
    public string? TitleId => Row.TitleId;
    public string? ContentId => Row.ContentId;
    public string Platform => Row.Platform;
    public string Finalization => Row.Finalization;
    public string? Version => Row.Version;
    public string Region => Row.Region ?? "Unknown";
    public string Category => Row.Category ?? string.Empty;
    public string Role => Row.Role.ToString();
    public string SizeText => Row.Size.ToString("n0");
    public string DecText => Row.Failed ? "err" : (Row.Decrypted ? "ok" : "no");
    public string FileOrNote => Row.Failed ? (Row.Note ?? "") : Path.GetFileName(Row.File);

    public static ScanResultRow From(PackageScanRow row, PackageLibraryReport report)
    {
        PackageLibraryWarning[] warnings = report.Warnings
            .Where(warning => warning.Files.Any(file => PathComparer.Equals(file, row.File)))
            .ToArray();
        string match = warnings.Length > 0
            ? string.Join("; ", warnings.Select(warning => warning.Kind == PackageLibraryWarningKind.MissingBase
                ? "Missing base"
                : "Region mismatch").Distinct())
            : row.Role is PackageLibraryRole.Update or PackageLibraryRole.Dlc
                ? "Matched"
                : string.Empty;
        return new ScanResultRow { Row = row, Match = match };
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
