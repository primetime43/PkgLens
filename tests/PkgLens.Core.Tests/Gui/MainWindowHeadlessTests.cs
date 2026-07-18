using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PkgLens.Core.Shared;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;
using PkgLens.Gui.Views.Pages;

namespace PkgLens.Core.Tests.Gui;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void OperationMenus_AreBuiltFromCatalogWithEligibilityBindings()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        window.Show();

        MenuItem fileMenu = window.FindControl<MenuItem>("FileMenu")!;
        MenuItem[] items = Descendants(fileMenu).ToArray();
        MenuItem open = Assert.Single(items, item => Equals(item.Tag, OperationId.OpenPackage));
        MenuItem close = Assert.Single(items, item => Equals(item.Tag, OperationId.ClosePackage));
        Assert.True(open.IsEnabled);
        Assert.False(close.IsEnabled);
        Assert.Equal(OperationCatalog.Get(OperationId.OpenPackage).Tooltip, ToolTip.GetTip(open));
        window.Close();
    }

    [AvaloniaFact]
    public void Shell_ComposesDedicatedPageControls()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        window.Show();

        Assert.NotNull(window.FindControl<HomePage>("HomePage"));
        Assert.NotNull(window.FindControl<SuggestedPage>("SuggestedPage"));
        Assert.NotNull(window.FindControl<PackagePage>("PackagePage"));
        Assert.NotNull(window.FindControl<KeysPage>("KeysPage"));

        PackPage packPage = window.FindControl<PackPage>("PackPage")!;
        ResignPage resignPage = window.FindControl<ResignPage>("ResignPage")!;
        DecryptPage decryptPage = window.FindControl<DecryptPage>("DecryptPage")!;
        Assert.NotNull(packPage.FindControl<ComboBox>("PackContentTypeBox"));
        Assert.NotNull(resignPage.FindControl<Control>("ResignMenu"));
        Assert.NotNull(decryptPage.FindControl<Button>("DecryptViewButton"));
        window.Close();
    }

    [AvaloniaFact]
    public void Navigation_StaysSynchronizedWithVisiblePage()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        ListBox rail = window.FindControl<ListBox>("ToolRail")!;
        Assert.Equal(7, rail.ItemCount);
        Assert.True(window.FindControl<Control>("HomePage")!.IsVisible);

        viewModel.ActiveTool = ToolPage.Keys;

        Assert.Equal((int)ToolPage.Keys, rail.SelectedIndex);
        Assert.True(window.FindControl<Control>("KeysPage")!.IsVisible);
        Assert.False(window.FindControl<Control>("HomePage")!.IsVisible);

        rail.SelectedIndex = (int)ToolPage.Decrypt;

        Assert.Equal(ToolPage.Decrypt, viewModel.ActiveTool);
        Assert.True(window.FindControl<Control>("DecryptPage")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void BusyState_DisablesToolsAndShowsCancelableProgress()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        viewModel.IsBusy = true;
        viewModel.CanCancel = true;
        viewModel.BusyMessage = "Verifying output…";

        Assert.True(window.FindControl<Grid>("OperationProgressPanel")!.IsVisible);
        Assert.False(window.FindControl<Grid>("ToolContentShell")!.IsEnabled);
        Assert.True(window.FindControl<Button>("OperationCancelButton")!.IsEnabled);

        viewModel.CanCancel = false;
        Assert.False(window.FindControl<Button>("OperationCancelButton")!.IsEnabled);

        viewModel.IsBusy = false;
        Assert.False(window.FindControl<Grid>("OperationProgressPanel")!.IsVisible);
        Assert.True(window.FindControl<Grid>("ToolContentShell")!.IsEnabled);
        window.Close();
    }

    private static IEnumerable<MenuItem> Descendants(MenuItem root)
    {
        foreach (object? item in root.Items)
        {
            if (item is not MenuItem menuItem)
                continue;
            yield return menuItem;
            foreach (MenuItem child in Descendants(menuItem))
                yield return child;
        }
    }
}
