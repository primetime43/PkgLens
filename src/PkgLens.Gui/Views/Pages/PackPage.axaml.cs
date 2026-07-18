using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views.Pages;

public partial class PackPage : UserControl
{
    public PackPage() => AvaloniaXamlLoader.Load(this);

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnPackBrowseFolder(object? sender, RoutedEventArgs e) => Host?.OnPackBrowseFolder(sender, e);
    private void OnPackBuild(object? sender, RoutedEventArgs e) => Host?.OnPackBuild(sender, e);
    private void OnPackFolderInfo(object? sender, RoutedEventArgs e) => Host?.OnPackFolderInfo(sender, e);
    private void OnPackPickRap(object? sender, RoutedEventArgs e) => Host?.OnPackPickRap(sender, e);
    private void OnPackDrmChanged(object? sender, NumericUpDownValueChangedEventArgs e) => Host?.OnPackDrmChanged(sender, e);
}
