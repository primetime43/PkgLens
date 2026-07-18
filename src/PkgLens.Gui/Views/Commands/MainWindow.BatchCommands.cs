using Avalonia.Interactivity;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private void OnBatchCenterClick(object? sender, RoutedEventArgs e) =>
        new BatchCenterDialog(Vm.KeysDirectory, Vm.RapDirectory).ShowDialog(this);

    private void OnHomeBatch(object? sender, RoutedEventArgs e) => OnBatchCenterClick(sender, e);

    private void OpenBatchCenter(string sourceDirectory) =>
        new BatchCenterDialog(Vm.KeysDirectory, Vm.RapDirectory, sourceDirectory).ShowDialog(this);
}
