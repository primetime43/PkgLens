using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PkgLens.Gui.Services;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public sealed class PackageKeyCoverageGuiTests
{
    [AvaloniaFact]
    public void ReportFiltersAndSelection_ShowKeySourceAndPendingChanges()
    {
        var report = new PackageKeyCoverageReport
        {
            PackagePath = "example.pkg", ScannedFiles = 3, OrdinaryFiles = 1,
            Items = [new("USRDIR/open.edat", "EDAT", KeyCoverageStatus.Verified, "Integrity verified", "TEST-ID", "Local klicensee database", "123456789ABC", true),
                new("USRDIR/locked.edat", "EDAT", KeyCoverageStatus.MissingKey, "Import matching RAP")],
        };
        var dialog = new PackageKeyCoverageDialog(report); dialog.Show();
        try
        {
            var rows = dialog.FindControl<DataGrid>("Results")!;
            Assert.Equal(2, rows.ItemsSource.Cast<KeyCoverageItem>().Count());
            rows.SelectedItem = report.Items[0];
            Assert.Contains("pending edit", dialog.FindControl<SelectableTextBlock>("Details")!.Text);
            Assert.Contains("Local klicensee", dialog.FindControl<SelectableTextBlock>("Details")!.Text);
            dialog.FindControl<CheckBox>("AttentionOnly")!.IsChecked = true;
            Assert.Equal("USRDIR/locked.edat", Assert.Single(rows.ItemsSource.Cast<KeyCoverageItem>()).Path);
            dialog.FindControl<CheckBox>("AttentionOnly")!.IsChecked = false;
            dialog.FindControl<TextBox>("Search")!.Text = "TEST-ID";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("USRDIR/open.edat", Assert.Single(rows.ItemsSource.Cast<KeyCoverageItem>()).Path);
            Assert.Contains("locked.edat", report.ToJson()); // Export retains all rows despite filters.
        }
        finally { dialog.Close(); }
    }
}
