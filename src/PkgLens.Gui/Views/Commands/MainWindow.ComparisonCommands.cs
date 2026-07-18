using Avalonia.Interactivity;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private void OnComparePackagesClick(object? sender, RoutedEventArgs e) =>
        new PackageComparisonDialog(Vm.KeysDirectory, Vm.Package?.FilePath).ShowDialog(this);
}
