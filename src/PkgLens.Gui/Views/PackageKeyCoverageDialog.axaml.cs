using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class PackageKeyCoverageDialog : Window
{
    public PackageKeyCoverageDialog() => AvaloniaXamlLoader.Load(this);
    public PackageKeyCoverageDialog(PackageKeyCoverageReport report) : this()
    { DataContext = report; RefreshRows(); }
    private void OnFilterChanged(object? sender, RoutedEventArgs e) => RefreshRows();
    private void RefreshRows()
    {
        if (DataContext is not PackageKeyCoverageReport report) return;
        string search = this.FindControl<TextBox>("Search")!.Text?.Trim() ?? "";
        bool attention = this.FindControl<CheckBox>("AttentionOnly")!.IsChecked == true;
        var rows = report.Items.Where(i => (!attention || i.NeedsAttention) &&
            (i.Path.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             (i.ContentId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
             (i.KeySource?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))).ToArray();
        this.FindControl<DataGrid>("Results")!.ItemsSource = rows;
        this.FindControl<TextBlock>("RowCount")!.Text = report.Items.Count == 0
            ? "No recognized protected files or plaintext executables were found."
            : $"Showing {rows.Length} of {report.Items.Count} files. Select a row for details.";
        this.FindControl<SelectableTextBlock>("Details")!.Text = "Select a file for its verification details and next steps.";
    }
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (this.FindControl<DataGrid>("Results")?.SelectedItem is KeyCoverageItem item)
            this.FindControl<SelectableTextBlock>("Details")!.Text = item.DisplayPath + "\n" + item.Description;
    }
    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PackageKeyCoverageReport report) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save key coverage report", SuggestedFileName = Path.GetFileNameWithoutExtension(report.PackagePath) + "-key-coverage.json",
            FileTypeChoices = [new FilePickerFileType("JSON report") { Patterns = ["*.json"] }, new FilePickerFileType("Text report") { Patterns = ["*.txt"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            AtomicOutput.EnsureDifferentPath(report.PackagePath, path);
            await AtomicOutput.WriteAllTextAsync(path, Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? report.ToText() : report.ToJson());
            this.FindControl<TextBlock>("RowCount")!.Text = "Saved complete report to " + path;
        }
        catch (Exception ex) { this.FindControl<TextBlock>("RowCount")!.Text = "Could not save report: " + ex.Message; }
    }
    private void OnRescan(object? sender, RoutedEventArgs e) => Close(true);
    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}
