using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views.Pages;

public partial class PackagePage : UserControl
{
    public PackagePage() => AvaloniaXamlLoader.Load(this);

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnRebuildSelectedEdat(object? sender, RoutedEventArgs e) => Host?.OnRebuildSelectedEdat(sender, e);

    private void OnOpenPackageClick(object? sender, RoutedEventArgs e) => Host?.OnHomeOpenPkg(sender, e);

    private void OnClearFileFilter(object? sender, RoutedEventArgs e) => Host?.OnClearFileFilter(sender, e);
    private void OnDecryptManual(object? sender, RoutedEventArgs e) => Host?.OnDecryptManual(sender, e);
    private void OnExtractClick(object? sender, RoutedEventArgs e) => Host?.OnExtractClick(sender, e);
    private void OnExploreTrophies(object? sender, RoutedEventArgs e) => Host?.OnExploreTrophies(sender, e);
    private void OnExtractPspIso(object? sender, RoutedEventArgs e) => Host?.OnExtractPspIso(sender, e);
    private void OnReplaceClick(object? sender, RoutedEventArgs e) => Host?.OnReplaceClick(sender, e);
    private void OnUnpackPbp(object? sender, RoutedEventArgs e) => Host?.OnUnpackPbp(sender, e);
    private void OnViewClick(object? sender, RoutedEventArgs e) => Host?.OnViewClick(sender, e);
    private void OnGridDoubleTapped(object? sender, TappedEventArgs e) => Host?.OnGridDoubleTapped(sender, e);
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e) => Host?.OnGridPointerPressed(sender, e);
}
