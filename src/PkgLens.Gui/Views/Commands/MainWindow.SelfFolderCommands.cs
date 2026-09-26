using Avalonia.Interactivity;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    internal void OnVerifySelfClick(object? sender, RoutedEventArgs e)
    {
        if (!Vm.IsBusy) new VerifySelfDialog(Vm.RapDirectory).ShowDialog(this);
    }

    internal void OnSelfFolderClick(object? sender, RoutedEventArgs e) =>
        new SelfFolderDialog(Vm.RapDirectory).ShowDialog(this);
}
