using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class RapManagerDialog : Window
{
    private string? _directory;
    private TextBlock _directoryText = null!;
    private TextBlock _status = null!;
    private TextBox _contentId = null!;
    private CheckBox _replace = null!;
    private DataGrid _grid = null!;
    private Button _remove = null!;

    public string? SelectedDirectory => _directory;

    public RapManagerDialog() : this(null) { }

    public RapManagerDialog(string? directory)
    {
        _directory = directory;
        InitializeComponent();
        _directoryText = this.FindControl<TextBlock>("DirectoryText")!;
        _status = this.FindControl<TextBlock>("StatusText")!;
        _contentId = this.FindControl<TextBox>("ContentIdBox")!;
        _replace = this.FindControl<CheckBox>("ReplaceCheck")!;
        _grid = this.FindControl<DataGrid>("Grid")!;
        _remove = this.FindControl<Button>("RemoveButton")!;
        RefreshEntries();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnChooseDirectory(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the RAP library folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _directory = Path.GetFullPath(path);
        RefreshEntries();
    }

    private void OnUseDefault(object? sender, RoutedEventArgs e)
    {
        _directory = null;
        RefreshEntries();
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import RAP license",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("RAP license") { Patterns = new[] { "*.rap", "*.RAP" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } source) return;

        try
        {
            string contentId = (_contentId.Text ?? string.Empty).Trim();
            if (contentId.Length == 0) contentId = Path.GetFileNameWithoutExtension(source);
            byte[] rap = await File.ReadAllBytesAsync(source);
            string installed = RapStore.Install(contentId, rap, _directory, overwrite: _replace.IsChecked == true);
            _contentId.Text = contentId;
            _status.Text = $"Imported {contentId} → {installed}";
            RefreshEntries(preserveStatus: true);
        }
        catch (Exception ex)
        {
            _status.Text = $"Import failed: {ex.Message}";
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not RapStoreRow row) return;
        try
        {
            bool removed = RapStore.RemoveEntry(row.Entry, _directory);
            _status.Text = removed ? $"Removed RAP for {row.ContentId}." : "The selected RAP no longer exists.";
            RefreshEntries(preserveStatus: true);
        }
        catch (Exception ex)
        {
            _status.Text = $"Remove failed: {ex.Message}";
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => RefreshEntries();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _remove.IsEnabled = _grid.SelectedItem is RapStoreRow;

    private void RefreshEntries(bool preserveStatus = false)
    {
        try
        {
            var entries = RapStore.List(_directory);
            _directoryText.Text = RapStore.DirectoryPath(_directory);
            _grid.ItemsSource = entries.Select(RapStoreRow.From).ToList();
            _remove.IsEnabled = false;
            if (!preserveStatus)
                _status.Text = $"{entries.Count} RAP(s): {entries.Count(entry => entry.IsValid)} valid, " +
                               $"{entries.Count(entry => !entry.IsValid)} invalid.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read the library: {ex.Message}";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
