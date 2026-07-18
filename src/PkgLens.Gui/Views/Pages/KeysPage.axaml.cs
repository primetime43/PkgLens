using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views.Pages;

public partial class KeysPage : UserControl
{
    public KeysPage() => AvaloniaXamlLoader.Load(this);

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnClearRapSearchFoldersClick(object? sender, RoutedEventArgs e) => Host?.OnClearRapSearchFoldersClick(sender, e);
    private void OnKeysClick(object? sender, RoutedEventArgs e) => Host?.OnKeysClick(sender, e);
    private void OnManageRapsClick(object? sender, RoutedEventArgs e) => Host?.OnManageRapsClick(sender, e);
    private void OnRapSearchFoldersClick(object? sender, RoutedEventArgs e) => Host?.OnRapSearchFoldersClick(sender, e);
    private void OnSetKeyClick(object? sender, RoutedEventArgs e) => Host?.OnSetKeyClick(sender, e);
}
