using Avalonia.Interactivity;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private void OnPackageOrganizerClick(object? sender, RoutedEventArgs e) =>
        new PackageOrganizerDialog(Vm.KeysDirectory).ShowDialog(this);

    private void OnHomeOrganizer(object? sender, RoutedEventArgs e) => OnPackageOrganizerClick(sender, e);
}
