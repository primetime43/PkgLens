using Avalonia.Interactivity;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private void OnGoFirmwarePatchClick(object? sender, RoutedEventArgs e)
    {
        Vm.ActiveTool = ToolPage.Resign;
        ShowResignOp("magic");
        Vm.Status = "Firmware patch tool ready. Select a SELF or EBOOT file to analyze and patch.";
    }

    private void OnGoBytePatchClick(object? sender, RoutedEventArgs e)
    {
        Vm.ActiveTool = ToolPage.Resign;
        ShowResignOp("byte");
        Vm.Status = "Advanced byte patch tool ready. Select a SELF or EBOOT file and review the patch carefully.";
    }
}
