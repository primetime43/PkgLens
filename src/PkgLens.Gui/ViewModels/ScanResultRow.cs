using System.IO;
using PkgLens.Core;
using PkgLens.Core.Shared;

namespace PkgLens.Gui.ViewModels;

/// <summary>Display wrapper over a <see cref="PackageScanRow"/> for the Scan dialog's grid.</summary>
public sealed class ScanResultRow
{
    public required PackageScanRow Row { get; init; }

    public string Title => Row.Failed ? "(unreadable)" : (Row.Title ?? Row.ContentId ?? "(unknown)");
    public string? TitleId => Row.TitleId;
    public string Platform => Row.Platform;
    public string Finalization => Row.Finalization;
    public string? Version => Row.Version;
    public string SizeText => Row.Size.ToString("n0");
    public string DecText => Row.Failed ? "err" : (Row.Decrypted ? "ok" : "no");
    public string FileOrNote => Row.Failed ? (Row.Note ?? "") : Path.GetFileName(Row.File);

    public static ScanResultRow From(PackageScanRow row) => new() { Row = row };
}
