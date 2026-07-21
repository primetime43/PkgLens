using System;
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
    private DataGrid _grid = null!;
    private Button _remove = null!;

    public KlicenseeManagerDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _path = this.FindControl<TextBlock>("PathText")!;
        _status = this.FindControl<TextBlock>("StatusText")!;
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
        if (_grid.SelectedItem is not KlicenseeStoreRow row) return;
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
        _remove.IsEnabled = _grid.SelectedItem is KlicenseeStoreRow;

    private void RefreshEntries(bool preserveStatus = false)
    {
        try
        {
            var entries = KlicenseeStore.List();
            _path.Text = KlicenseeStore.DatabasePath();
            _grid.ItemsSource = entries.Select(KlicenseeStoreRow.From).ToList();
            _remove.IsEnabled = false;
            if (!preserveStatus)
                _status.Text = $"{entries.Count} saved mapping{(entries.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex)
        {
            _path.Text = KlicenseeStore.DatabasePath();
            _status.Text = $"Could not read the library: {ex.Message}";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
