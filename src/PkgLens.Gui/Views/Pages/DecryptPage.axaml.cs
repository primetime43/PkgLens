using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views.Pages;

public partial class DecryptPage : UserControl
{
    public DecryptPage() => AvaloniaXamlLoader.Load(this);

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnDecryptBrowseFile(object? sender, RoutedEventArgs e) => Host?.OnDecryptBrowseFile(sender, e);
    private void OnDecryptBrowseRap(object? sender, RoutedEventArgs e) => Host?.OnDecryptBrowseRap(sender, e);
    private void OnDecryptRun(object? sender, RoutedEventArgs e) => Host?.OnDecryptRun(sender, e);
    private void OnDecryptView(object? sender, RoutedEventArgs e) => Host?.OnDecryptView(sender, e);
    private void OnShowResultInFolder(object? sender, RoutedEventArgs e) => Host?.OnShowResultInFolder(sender, e);
}
