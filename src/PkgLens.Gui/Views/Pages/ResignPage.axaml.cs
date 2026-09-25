using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views.Pages;

public partial class ResignPage : UserControl
{
    public ResignPage() => AvaloniaXamlLoader.Load(this);

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnBytePatch(object? sender, RoutedEventArgs e) => Host?.OnBytePatch(sender, e);
    private void OnMagicPatch(object? sender, RoutedEventArgs e) => Host?.OnMagicPatch(sender, e);
    private void OnMakeFself(object? sender, RoutedEventArgs e) => Host?.OnMakeFself(sender, e);
    private void OnBuildSelfPickRap(object? sender, RoutedEventArgs e) => Host?.OnBuildSelfPickRap(sender, e);
    private void OnPickRap(object? sender, RoutedEventArgs e) => Host?.OnPickRap(sender, e);
    private void OnResignBack(object? sender, RoutedEventArgs e) => Host?.OnResignBack(sender, e);
    private void OnResignCardClick(object? sender, RoutedEventArgs e) => Host?.OnResignCardClick(sender, e);
    private void OnSelfBrowse(object? sender, RoutedEventArgs e) => Host?.OnSelfBrowse(sender, e);
    private void OnShowResultInFolder(object? sender, RoutedEventArgs e) => Host?.OnShowResultInFolder(sender, e);
    private void OnUnself(object? sender, RoutedEventArgs e) => Host?.OnUnself(sender, e);
}
