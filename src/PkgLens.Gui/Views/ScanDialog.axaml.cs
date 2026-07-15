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
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

/// <summary>Batch-scans a folder of .pkg files into a catalog grid, with CSV/JSON export.</summary>
public partial class ScanDialog : Window
{
    private readonly string? _keysDir;
    private string? _folder;
    private List<PackageScanRow> _rows = new();

    private TextBlock _folderText = null!;
    private TextBlock _status = null!;
    private CheckBox _recursive = null!;
    private DataGrid _grid = null!;
    private Button _scanBtn = null!, _csvBtn = null!, _jsonBtn = null!;
    private StackPanel _inputPanel = null!;
    private Button _cancelBtn = null!;
    private ProgressBar _progress = null!;
    private CancellationTokenSource? _cancellation;

    public ScanDialog() : this(null) { }

    public ScanDialog(string? keysDir)
    {
        InitializeComponent();
        _keysDir = keysDir;
        _folderText = this.FindControl<TextBlock>("FolderText")!;
        _status = this.FindControl<TextBlock>("StatusText")!;
        _recursive = this.FindControl<CheckBox>("RecursiveCheck")!;
        _grid = this.FindControl<DataGrid>("Grid")!;
        _scanBtn = this.FindControl<Button>("ScanBtn")!;
        _csvBtn = this.FindControl<Button>("CsvBtn")!;
        _jsonBtn = this.FindControl<Button>("JsonBtn")!;
        _inputPanel = this.FindControl<StackPanel>("InputPanel")!;
        _cancelBtn = this.FindControl<Button>("CancelBtn")!;
        _progress = this.FindControl<ProgressBar>("ScanProgress")!;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder of .pkg files to scan",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } dir)
            return;
        _folder = dir;
        _folderText.Text = dir;
        _scanBtn.IsEnabled = true;
    }

    private async void OnScan(object? sender, RoutedEventArgs e)
    {
        if (_folder is null) return;

        string folder = _folder;
        bool recursive = _recursive.IsChecked == true;
        string? keysDir = _keysDir;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, "Scanning…", indeterminate: true);
        IProgress<(int Completed, int Total, string Name)> progress =
            new Progress<(int Completed, int Total, string Name)>(value =>
        {
            _progress.IsIndeterminate = false;
            _progress.Value = value.Total == 0 ? 100 : value.Completed * 100d / value.Total;
            _status.Text = $"Scanning {value.Name} ({value.Completed}/{value.Total})…";
        });

        try
        {
            var rows = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.EnumerateFiles(folder, "*", search)
                    .Where(f => Path.GetExtension(f).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                IKeyProvider keys = new FileKeyProvider(keysDir);
                var results = new List<PackageScanRow>(files.Count);
                for (int index = 0; index < files.Count; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    string path = files[index];
                    results.Add(PackageScanner.Inspect(path, keys));
                    progress.Report((index + 1, files.Count, Path.GetFileName(path)));
                }
                return results;
            }, cancellation.Token);

            _rows = rows;
            _grid.ItemsSource = rows.Select(ScanResultRow.From).ToList();
            long total = rows.Sum(r => r.Size);
            int ok = rows.Count(r => r.Decrypted), failed = rows.Count(r => r.Failed);
            _status.Text = rows.Count == 0
                ? "No .pkg files found."
                : $"{rows.Count} package(s), {total:n0} bytes — {ok} decrypted" + (failed > 0 ? $", {failed} unreadable" : "") + ".";
            _csvBtn.IsEnabled = _jsonBtn.IsEnabled = rows.Count > 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Scan failed: {ex.Message}";
            await ShowError("Scan failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
                _cancellation = null;
            SetBusy(false, _status.Text ?? string.Empty);
        }
    }

    private async void OnSaveCsv(object? sender, RoutedEventArgs e) => await Save("csv", "scan.csv", PackageScanner.ToCsv(_rows));
    private async void OnSaveJson(object? sender, RoutedEventArgs e) => await Save("json", "scan.json", PackageScanner.ToJson(_rows));

    private async Task Save(string ext, string suggested, string content)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save scan as {ext.ToUpperInvariant()}…",
            SuggestedFileName = suggested,
            DefaultExtension = ext,
        });
        if (file?.TryGetLocalPath() is not { } dest)
            return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, $"Saving {Path.GetFileName(dest)}…", indeterminate: true);
        try
        {
            await AtomicOutput.WriteAllTextAsync(dest, content, cancellation.Token);
            _status.Text = $"Saved {Path.GetFileName(dest)}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Text = "Save cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Save failed: {ex.Message}";
            await ShowError("Save failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
                _cancellation = null;
            SetBusy(false, _status.Text ?? string.Empty);
        }
    }

    private void SetBusy(bool busy, string status, bool indeterminate = false)
    {
        _status.Text = status;
        _inputPanel.IsEnabled = !busy;
        _grid.IsEnabled = !busy;
        _csvBtn.IsEnabled = !busy && _rows.Count > 0;
        _jsonBtn.IsEnabled = !busy && _rows.Count > 0;
        _cancelBtn.IsVisible = busy;
        _cancelBtn.IsEnabled = busy;
        _progress.IsVisible = busy;
        _progress.IsIndeterminate = indeterminate;
        if (!busy)
            _progress.Value = 0;
    }

    private async Task ShowError(string title, Exception exception) =>
        await new ErrorDialog(new GuiErrorReport(title, exception.Message, exception.ToString())).ShowDialog(this);

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cancelBtn.IsEnabled = false;
        _status.Text = "Cancelling…";
        _cancellation?.Cancel();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
