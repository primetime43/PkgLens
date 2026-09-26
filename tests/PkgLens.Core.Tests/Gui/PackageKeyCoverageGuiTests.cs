using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public sealed class PackageKeyCoverageGuiTests
{
    [AvaloniaFact]
    public async Task CommandAndRescanCompleteWithoutAccessingWindowFromWorkerThread()
    {
        string source = Path.Combine(Path.GetTempPath(), "pkglens-coverage-thread-" + Guid.NewGuid() + ".pkg");
        byte[] self = EncryptedSelfBuilder.Build(DevKlicFixture.Elf(1024), new());
        File.WriteAllBytes(source, new SyntheticPkgBuilder().AddFile("USRDIR/EBOOT.BIN", self).Build());
        using var package = PackageViewModel.Load(source, new InMemoryKeyProvider());
        var model = new MainWindowViewModel { Package = package };
        var window = new MainWindow { DataContext = model };
        GuiErrorReport? error = null;
        model.ErrorRequested += (_, report) => error = report;
        try
        {
            window.Show();
            window.OnPackageKeyCoverage(null, new());
            for (int scan = 0; scan < 2; scan++)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (window.OwnedWindows.OfType<PackageKeyCoverageDialog>().FirstOrDefault() is null
                    && error is null && DateTime.UtcNow < deadline)
                    await Task.Delay(10);
                Assert.True(error is null, error?.Details);
                var dialog = Assert.Single(window.OwnedWindows.OfType<PackageKeyCoverageDialog>());
                var report = Assert.IsType<PackageKeyCoverageReport>(dialog.DataContext);
                Assert.Equal(KeyCoverageStatus.Opens, Assert.Single(report.Items).Status);
                Assert.False(model.IsBusy);
                dialog.Close(scan == 0); // First result requests the real Scan again loop.
                await Task.Yield();
            }
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            model.Package = null;
            window.Close();
            package.Dispose();
            File.Delete(source);
        }
    }

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
