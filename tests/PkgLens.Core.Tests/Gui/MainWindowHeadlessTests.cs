using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void Navigation_StaysSynchronizedWithVisiblePage()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        ListBox rail = window.FindControl<ListBox>("ToolRail")!;
        Assert.Equal(7, rail.ItemCount);
        Assert.True(window.FindControl<ScrollViewer>("HomePage")!.IsVisible);

        viewModel.ActiveTool = ToolPage.Keys;

        Assert.Equal((int)ToolPage.Keys, rail.SelectedIndex);
        Assert.True(window.FindControl<ScrollViewer>("KeysPage")!.IsVisible);
        Assert.False(window.FindControl<ScrollViewer>("HomePage")!.IsVisible);

        rail.SelectedIndex = (int)ToolPage.Decrypt;

        Assert.Equal(ToolPage.Decrypt, viewModel.ActiveTool);
        Assert.True(window.FindControl<ScrollViewer>("DecryptPage")!.IsVisible);
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
}
