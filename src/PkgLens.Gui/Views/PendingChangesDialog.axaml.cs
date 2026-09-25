using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class PendingChangesDialog : Window
{
    public PendingChangesDialog() => AvaloniaXamlLoader.Load(this);

    private void OnRevert(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PackageViewModel package && sender is Button { Tag: PendingPackageChange change })
            package.Operations.RevertEntry(change.Entry);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);
}
