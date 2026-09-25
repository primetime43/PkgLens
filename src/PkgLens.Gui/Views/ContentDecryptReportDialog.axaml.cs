using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core.Shared;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class ContentDecryptReportDialog : Window
{
    public ContentDecryptReportDialog() => AvaloniaXamlLoader.Load(this);

    public ContentDecryptReportDialog(ContentDecryptReport report) : this()
    {
        DataContext = report;
        this.FindControl<CheckBox>("AttentionOnly")!.IsChecked = report.AttentionCount > 0;
        RefreshRows();
    }

    private void OnFilterChanged(object? sender, RoutedEventArgs e) => RefreshRows();

    private void RefreshRows()
    {
        if (DataContext is not ContentDecryptReport report) return;
        this.FindControl<DataGrid>("Results")!.ItemsSource = this.FindControl<CheckBox>("AttentionOnly")!.IsChecked == true
            ? report.Items.Where(item => item.Status is not (ContentDecryptStatus.Extracted or ContentDecryptStatus.Decrypted)).ToArray()
            : report.Items;
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ContentDecryptReport report) return;
        try { Process.Start(new ProcessStartInfo { FileName = report.OutputDirectory, UseShellExecute = true }); }
        catch (Exception ex)
        {
            await new ErrorDialog(new GuiErrorReport("Could not open output folder", ex.Message, ex.ToString())).ShowDialog(this);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
