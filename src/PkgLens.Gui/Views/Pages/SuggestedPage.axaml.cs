using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views.Pages;

public partial class SuggestedPage : UserControl
{
    public SuggestedPage() => AvaloniaXamlLoader.Load(this);

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnBrowsePackageClick(object? sender, RoutedEventArgs e) => Host?.OnBrowsePackageClick(sender, e);
    private void OnRecommendedActionClick(object? sender, RoutedEventArgs e) => Host?.OnRecommendedActionClick(sender, e);
}
