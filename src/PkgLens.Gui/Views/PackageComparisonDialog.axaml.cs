using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class PackageComparisonDialog : Window
{
    private readonly string? _keysDirectory;
    private string? _basePath;
    private string? _targetPath;
    private PackageComparisonResult? _comparison;
    private CancellationTokenSource? _cancellation;

    private Control _setup = null!;
    private TextBlock _basePathText = null!;
    private TextBlock _targetPathText = null!;
    private TextBlock _summary = null!;
    private TextBlock _warning = null!;
    private TextBlock _status = null!;
    private Control _summaryPanel = null!;
    private DataGrid _filesGrid = null!;
    private DataGrid _sfoGrid = null!;
    private CheckBox _showUnchanged = null!;
    private Button _compare = null!;
    private Button _export = null!;
    private Button _build = null!;
    private Button _cancel = null!;
    private Button _close = null!;
    private ProgressBar _progress = null!;

    public PackageComparisonDialog() : this(null, null) { }

    public PackageComparisonDialog(string? keysDirectory, string? basePackagePath)
    {
        _keysDirectory = keysDirectory;
        InitializeComponent();
        FindControls();
        if (!string.IsNullOrWhiteSpace(basePackagePath) && File.Exists(basePackagePath))
            SetBase(basePackagePath);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindControls()
    {
        _setup = this.FindControl<Control>("SetupPanel")!;
        _basePathText = this.FindControl<TextBlock>("BasePathText")!;
        _targetPathText = this.FindControl<TextBlock>("TargetPathText")!;
        _summary = this.FindControl<TextBlock>("SummaryText")!;
        _warning = this.FindControl<TextBlock>("WarningText")!;
        _status = this.FindControl<TextBlock>("StatusText")!;
        _summaryPanel = this.FindControl<Control>("SummaryPanel")!;
        _filesGrid = this.FindControl<DataGrid>("FilesGrid")!;
        _sfoGrid = this.FindControl<DataGrid>("SfoGrid")!;
        _showUnchanged = this.FindControl<CheckBox>("ShowUnchangedCheck")!;
        _compare = this.FindControl<Button>("CompareButton")!;
        _export = this.FindControl<Button>("ExportButton")!;
        _build = this.FindControl<Button>("BuildButton")!;
        _cancel = this.FindControl<Button>("CancelButton")!;
        _close = this.FindControl<Button>("CloseButton")!;
        _progress = this.FindControl<ProgressBar>("Progress")!;
    }

    private async void OnChooseBase(object? sender, RoutedEventArgs e)
    {
        string? path = await PickPackage("Choose the base or older package");
        if (path is not null) SetBase(path);
    }

    private async void OnChooseTarget(object? sender, RoutedEventArgs e)
    {
        string? path = await PickPackage("Choose the newer or modified target package");
        if (path is not null) SetTarget(path);
    }

    private async Task<string?> PickPackage(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PlayStation package") { Patterns = ["*.pkg"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void SetBase(string path)
    {
        _basePath = Path.GetFullPath(path);
        _basePathText.Text = _basePath;
        ToolTip.SetTip(_basePathText, _basePath);
        ClearComparison("Base selection changed. Compare again to refresh results.");
    }

    private void SetTarget(string path)
    {
        _targetPath = Path.GetFullPath(path);
        _targetPathText.Text = _targetPath;
        ToolTip.SetTip(_targetPathText, _targetPath);
        ClearComparison("Target selection changed. Compare again to refresh results.");
    }

    private void OnSwap(object? sender, RoutedEventArgs e)
    {
        (_basePath, _targetPath) = (_targetPath, _basePath);
        _basePathText.Text = _basePath ?? "Choose the base or older package";
        _targetPathText.Text = _targetPath ?? "Choose the newer or modified target package";
        ToolTip.SetTip(_basePathText, _basePath);
        ToolTip.SetTip(_targetPathText, _targetPath);
        ClearComparison("Selections swapped. Compare again to refresh results.");
    }

    private async void OnCompare(object? sender, RoutedEventArgs e)
    {
        if (_basePath is null || _targetPath is null) return;
        try
        {
            SetBusy(true, "Hashing decrypted package files…");
            var guiProgress = CreateProgress();
            var keys = new FileKeyProvider(_keysDirectory);
            _comparison = await Task.Run(() => PackageComparison.Compare(
                _basePath, _targetPath, keys, _cancellation!.Token, guiProgress));
            ShowComparison();
            SetBusy(false, $"Compared {_comparison.Files.Count:n0} paths and {_comparison.SfoValues.Count:n0} SFO values.");
        }
        catch (OperationCanceledException) { SetBusy(false, "Comparison cancelled."); }
        catch (Exception exception) { SetBusy(false, "Comparison failed."); await ShowError("Could not compare packages", exception); }
    }

    private void ShowComparison()
    {
        if (_comparison is null) return;
        string from = Describe(_comparison.Base);
        string to = Describe(_comparison.Target);
        _summary.Text = $"{from}  →  {to}\n" +
                        $"{_comparison.ModifiedCount:n0} modified · {_comparison.AddedCount:n0} added · " +
                        $"{_comparison.RemovedCount:n0} removed · {_comparison.UnchangedCount:n0} unchanged · " +
                        $"approximately {FormatSize(_comparison.OverlayPayloadBytes)} changed payload";
        _warning.Text = _comparison.Warnings.Count == 0
            ? "No title, platform, or version-order warnings detected."
            : string.Join("\n", _comparison.Warnings.Select(warning => "⚠ " + warning));
        _warning.Foreground = _comparison.Warnings.Count == 0 ? null : Avalonia.Media.Brushes.DarkOrange;
        _summaryPanel.IsVisible = true;
        _showUnchanged.IsEnabled = true;
        RefreshGrids();
        UpdateButtons();
    }

    private void OnShowUnchangedChanged(object? sender, RoutedEventArgs e) => RefreshGrids();

    private void RefreshGrids()
    {
        if (_comparison is null) { _filesGrid.ItemsSource = null; _sfoGrid.ItemsSource = null; return; }
        bool showUnchanged = _showUnchanged.IsChecked == true;
        _filesGrid.ItemsSource = _comparison.Files
            .Where(file => showUnchanged || file.Change != PackageFileChange.Unchanged)
            .Select(PackageFileDisplayRow.From).ToArray();
        _sfoGrid.ItemsSource = _comparison.SfoValues
            .Where(value => showUnchanged || value.Change != "Unchanged")
            .ToArray();
    }

    private async void OnExportReport(object? sender, RoutedEventArgs e)
    {
        if (_comparison is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export package comparison report",
            SuggestedFileName = $"{_comparison.Target.TitleId ?? "package"}-comparison.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON report") { Patterns = ["*.json"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            await AtomicOutput.WriteAllTextAsync(path, PackageComparison.ToJson(_comparison));
            _status.Text = $"Saved comparison report → {path}";
        }
        catch (Exception exception) { await ShowError("Could not save comparison report", exception); }
    }

    private async void OnBuildOverlay(object? sender, RoutedEventArgs e)
    {
        if (_comparison is not { CanBuildOverlay: true } comparison) return;
        string removalText = comparison.RemovedCount == 0
            ? "No removals were detected."
            : $"{comparison.RemovedCount} removed file(s) will remain listed in the report but cannot be deleted by an overlay package.";
        bool confirmed = await new ConfirmationDialog(
            "Create compact overlay package?",
            $"PkgLens will include {comparison.OverlayContentFileCount} added or modified content file(s), plus a regenerated patch-category PARAM.SFO. {removalText}\n\n" +
            "The output is intended for CFW/HEN or RPCS3 and is not signed for stock firmware. Both compared packages remain unchanged.",
            "Choose output…").ShowDialog<bool>(this);
        if (!confirmed) return;

        string version = comparison.Target.Version?.Replace('.', '-') ?? "modified";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save compact overlay package as…",
            SuggestedFileName = $"{comparison.Target.TitleId ?? "package"}-{version}-overlay.pkg",
            DefaultExtension = "pkg",
            FileTypeChoices = [new FilePickerFileType("PS3 package") { Patterns = ["*.pkg"] }],
        });
        if (file?.TryGetLocalPath() is not { } outputPath) return;

        try
        {
            SetBusy(true, "Creating compact overlay package…");
            var guiProgress = CreateProgress();
            var keys = new FileKeyProvider(_keysDirectory);
            PackageOverlayBuildResult result = await Task.Run(() => PackageComparison.BuildOverlay(
                comparison, outputPath, keys, _cancellation!.Token, guiProgress));
            SetBusy(false, $"Created and verified {Path.GetFileName(result.OutputPath)} · " +
                           $"{result.IncludedFileCount:n0} files · {FormatSize(result.IncludedPayloadBytes)} payload. " +
                           "Original packages are unchanged.");
        }
        catch (OperationCanceledException) { SetBusy(false, "Overlay build cancelled; unfinished output was removed."); }
        catch (Exception exception) { SetBusy(false, "Overlay build failed."); await ShowError("Could not create overlay package", exception); }
    }

    private IProgress<PkgOperationProgress> CreateProgress() => new Progress<PkgOperationProgress>(value =>
    {
        _progress.IsIndeterminate = value.Total <= 0;
        if (value.Total > 0) _progress.Value = value.Percent;
        _status.Text = $"{value.Item ?? "Processing package"} · {value.Percent:0}%";
    });

    private void ClearComparison(string status)
    {
        _comparison = null;
        _filesGrid.ItemsSource = null;
        _sfoGrid.ItemsSource = null;
        _summaryPanel.IsVisible = false;
        _showUnchanged.IsEnabled = false;
        _status.Text = status;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        _compare.IsEnabled = !busy && _basePath is not null && _targetPath is not null;
        _export.IsEnabled = !busy && _comparison is not null;
        _build.IsEnabled = !busy && _comparison is { CanBuildOverlay: true };
        _showUnchanged.IsEnabled = !busy && _comparison is not null;
        _close.IsEnabled = !busy;
        _setup.IsEnabled = !busy;
        _filesGrid.IsEnabled = !busy;
        _sfoGrid.IsEnabled = !busy;
    }

    private void SetBusy(bool busy, string status)
    {
        if (busy)
        {
            _cancellation = new CancellationTokenSource();
            _progress.Value = 0;
        }
        else
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
        _status.Text = status;
        _progress.IsVisible = busy;
        _progress.IsIndeterminate = busy;
        _cancel.IsVisible = busy;
        _cancel.IsEnabled = busy;
        UpdateButtons();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cancel.IsEnabled = false;
        _status.Text = "Cancelling at a safe checkpoint…";
        _cancellation?.Cancel();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_cancellation is null) Close();
    }

    private Task ShowError(string title, Exception exception) =>
        new ErrorDialog(new GuiErrorReport(title, exception.Message, exception.ToString())).ShowDialog(this);

    private static string Describe(PackageComparisonSide side) =>
        $"{side.Title ?? side.TitleId ?? Path.GetFileName(side.FilePath)} " +
        $"({side.TitleId ?? "unknown ID"}, {side.Version ?? "unknown version"}, {side.Role})";

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:n0} B" : $"{value:0.##} {units[unit]}";
    }

    private sealed record PackageFileDisplayRow(
        string Change, string Path, string BaseSize, string TargetSize, string BaseHash, string TargetHash)
    {
        public static PackageFileDisplayRow From(PackageFileDifference difference) => new(
            difference.Change.ToString(),
            difference.Path,
            difference.BaseSize is ulong baseSize ? FormatSize(checked((long)baseSize)) : "—",
            difference.TargetSize is ulong targetSize ? FormatSize(checked((long)targetSize)) : "—",
            ShortHash(difference.BaseSha256),
            ShortHash(difference.TargetSha256));

        private static string ShortHash(string? hash) => hash is null ? "—" : hash[..Math.Min(hash.Length, 16)] + "…";
    }
}
