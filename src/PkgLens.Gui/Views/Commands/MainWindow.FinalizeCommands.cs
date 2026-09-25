using Avalonia.Interactivity;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    internal void OnFinalizePackageClick(object? sender, RoutedEventArgs e)
    {
        if (!Vm.IsBusy) new FinalizePackageDialog(Vm.Package?.FilePath).ShowDialog(this);
    }
}
