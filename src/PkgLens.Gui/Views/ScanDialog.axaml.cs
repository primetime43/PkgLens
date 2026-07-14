using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core;
using PkgLens.Core.Keys;
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

        _scanBtn.IsEnabled = false;
        _status.Text = "Scanning…";
        string folder = _folder;
        bool recursive = _recursive.IsChecked == true;
        string? keysDir = _keysDir;

        try
        {
            var rows = await Task.Run(() =>
            {
                var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.EnumerateFiles(folder, "*", search)
                    .Where(f => Path.GetExtension(f).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                IKeyProvider keys = new FileKeyProvider(keysDir);
                return files.Select(f => PackageScanner.Inspect(f, keys)).ToList();
            });

            _rows = rows;
            _grid.ItemsSource = rows.Select(ScanResultRow.From).ToList();
            long total = rows.Sum(r => r.Size);
            int ok = rows.Count(r => r.Decrypted), failed = rows.Count(r => r.Failed);
            _status.Text = rows.Count == 0
                ? "No .pkg files found."
                : $"{rows.Count} package(s), {total:n0} bytes — {ok} decrypted" + (failed > 0 ? $", {failed} unreadable" : "") + ".";
            _csvBtn.IsEnabled = _jsonBtn.IsEnabled = rows.Count > 0;
        }
        catch (Exception ex)
        {
            _status.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _scanBtn.IsEnabled = true;
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
        try
        {
            await File.WriteAllTextAsync(dest, content);
            _status.Text = $"Saved {Path.GetFileName(dest)}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Save failed: {ex.Message}";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
