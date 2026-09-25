using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;
using PkgLens.Gui.Views.Pages;

namespace PkgLens.Core.Tests.Gui;

public sealed class ContentDecryptGuiTests
{
    [AvaloniaFact]
    public void Report_DefaultsToActionableFailures_AndCanShowAllFiles()
    {
        var report = new ContentDecryptReport
        {
            OutputDirectory = "Example-decrypted",
            Items = [new("USRDIR/EBOOT.BIN", "SELF", ContentDecryptStatus.Decrypted, null, "ELF exported."),
                new("USRDIR/data.edat", "EDAT", ContentDecryptStatus.MissingKey, "UP0001-NPUB12345_00-TESTCONTENT00001", "Import the matching RAP.")],
        };
        var dialog = new ContentDecryptReportDialog(report);
        dialog.Show();
        var grid = dialog.FindControl<DataGrid>("Results")!;
        var filter = dialog.FindControl<CheckBox>("AttentionOnly")!;
        Assert.True(filter.IsChecked);
        Assert.Equal(ContentDecryptStatus.MissingKey, Assert.Single(grid.ItemsSource.Cast<ContentDecryptItem>()).Status);
        filter.IsChecked = false;
        Assert.Equal(2, grid.ItemsSource.Cast<ContentDecryptItem>().Count());
        dialog.Close();
    }

    [AvaloniaFact]
    public void PackageAction_IsEnabledOnlyWithReadablePackage_InMenuAndDecryptPage()
    {
        string source = Path.Combine(Path.GetTempPath(), "pkglens-content-ui-" + Guid.NewGuid().ToString("N") + ".pkg");
        File.WriteAllBytes(source, new SyntheticPkgBuilder().AddFile("readme.txt", "data").Build());
        var model = new MainWindowViewModel { ActiveTool = ToolPage.Decrypt };
        var window = new MainWindow { DataContext = model };
        try
        {
            window.Show();
            var page = window.FindControl<DecryptPage>("DecryptPage")!;
            var button = page.FindControl<Button>("DecryptPackageButton")!;
            var fileMenu = window.FindControl<MenuItem>("FileMenu")!;
            fileMenu.IsSubMenuOpen = true;
            var group = fileMenu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "_Extract and repack");
            group.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var menu = Descendants(fileMenu).Single(item => Equals(item.Tag, OperationId.DecryptPackageContents));
            Assert.False(menu.IsEnabled);
            Assert.False(button.IsEnabled);
            model.Package = PackageViewModel.Load(source, new InMemoryKeyProvider());
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsEnabled);
            Assert.True(button.IsEnabled);
            model.Package = null;
            Dispatcher.UIThread.RunJobs();
            Assert.False(menu.IsEnabled);
            Assert.False(button.IsEnabled);
        }
        finally
        {
            model.Package = null;
            window.Close();
            File.Delete(source);
        }
    }

    private static IEnumerable<MenuItem> Descendants(MenuItem parent) => parent.Items.OfType<MenuItem>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
