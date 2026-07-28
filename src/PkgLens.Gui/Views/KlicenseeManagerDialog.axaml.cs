using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class KlicenseeManagerDialog : Window
{
    private TextBlock _path = null!;
    private TextBlock _status = null!;
    private TextBox _filter = null!;
    private DataGrid _grid = null!;
    private Button _remove = null!;
    private List<KlicenseeStoreRow> _rows = [];
    private int _savedCount;
    private int _bundledCount;

    public KlicenseeManagerDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _path = this.FindControl<TextBlock>("PathText")!;
        _status = this.FindControl<TextBlock>("StatusText")!;
        _filter = this.FindControl<TextBox>("FilterBox")!;
        _grid = this.FindControl<DataGrid>("Grid")!;
        _remove = this.FindControl<Button>("RemoveButton")!;
        RefreshEntries();
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import an annotated klicensee list",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Klicensee lists") { Patterns = new[] { "*.txt", "*.ini" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } source) return;

        try
        {
            KlicenseeImportResult result = await Task.Run(() => KlicenseeStore.ImportFile(source));
            _status.Text = $"Imported {result.Imported}, updated {result.Replaced}, skipped {result.Skipped}" +
                           (result.Errors.Count == 0 ? "." : $"; {result.Errors.Count} warning(s): {result.Errors[0]}");
            RefreshEntries(preserveStatus: true);
        }
        catch (Exception ex)
        {
            _status.Text = $"Import failed: {ex.Message}";
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not KlicenseeStoreRow { CanRemove: true } row) return;
        try
        {
            bool removed = KlicenseeStore.Remove(row.Entry.Id);
            _status.Text = removed
                ? $"Removed mapping for {row.ContentId} / {row.FileName}."
                : "The selected mapping no longer exists.";
            RefreshEntries(preserveStatus: true);
        }
        catch (Exception ex)
        {
            _status.Text = $"Remove failed: {ex.Message}";
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => RefreshEntries();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _remove.IsEnabled = _grid.SelectedItem is KlicenseeStoreRow { CanRemove: true };

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_filter is null || _grid is null) return;
        ApplyFilter(updateStatus: true);
    }

    private void RefreshEntries(bool preserveStatus = false)
    {
        try
        {
            var saved = KlicenseeStore.List();
            var bundled = KnownKlicenseeStore.List();
            _savedCount = saved.Count;
            _bundledCount = bundled.Count;
            _rows = saved.Concat(bundled)
                .Select(KlicenseeStoreRow.From)
                .OrderBy(row => row.IsBundled)
                .ThenBy(row => row.TitleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.ContentId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _path.Text = KlicenseeStore.DatabasePath();
            ApplyFilter(updateStatus: !preserveStatus);
            _remove.IsEnabled = false;
        }
        catch (Exception ex)
        {
            _path.Text = KlicenseeStore.DatabasePath();
            _status.Text = $"Could not read the library: {ex.Message}";
        }
    }

    private void ApplyFilter(bool updateStatus)
    {
        string query = _filter.Text?.Trim() ?? string.Empty;
        List<KlicenseeStoreRow> visible = query.Length == 0
            ? _rows
            : _rows.Where(row => Matches(row, query)).ToList();
        _grid.ItemsSource = visible;
        _remove.IsEnabled = false;

        if (!updateStatus) return;
        string summary = $"{_savedCount} saved mapping{(_savedCount == 1 ? string.Empty : "s")}; " +
                         $"{_bundledCount} bundled identifier mappings.";
        _status.Text = query.Length == 0
            ? summary
            : $"Showing {visible.Count} of {_rows.Count} mappings. {summary}";
    }

    private static bool Matches(KlicenseeStoreRow row, string query) =>
        row.ContentId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.TitleId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.License.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.Fingerprint.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.Source.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
