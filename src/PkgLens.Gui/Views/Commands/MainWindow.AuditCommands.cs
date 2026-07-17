using Avalonia.Interactivity;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private void OnKeyLicenseAuditClick(object? sender, RoutedEventArgs e) =>
        new KeyLicenseAuditDialog(Vm.KeysDirectory).ShowDialog(this);
}
