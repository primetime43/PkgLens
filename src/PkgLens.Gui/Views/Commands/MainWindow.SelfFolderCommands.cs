using Avalonia.Interactivity;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    internal void OnSelfFolderClick(object? sender, RoutedEventArgs e) =>
        new SelfFolderDialog(Vm.RapDirectory).ShowDialog(this);
}
