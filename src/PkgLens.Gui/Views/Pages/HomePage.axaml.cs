using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views.Pages;

public partial class HomePage : UserControl
{
    public HomePage() => AvaloniaXamlLoader.Load(this);

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnClearRecentPackages(object? sender, RoutedEventArgs e) => Host?.OnClearRecentPackages(sender, e);
    private void OnHomeConvertCfw(object? sender, RoutedEventArgs e) => Host?.OnHomeConvertCfw(sender, e);
    private void OnHomeDecrypt(object? sender, RoutedEventArgs e) => Host?.OnHomeDecrypt(sender, e);
    private void OnHomeExportPs1(object? sender, RoutedEventArgs e) => Host?.OnHomeExportPs1(sender, e);
    private void OnHomeExportPsp(object? sender, RoutedEventArgs e) => Host?.OnHomeExportPsp(sender, e);
    private void OnHomeExportVita(object? sender, RoutedEventArgs e) => Host?.OnHomeExportVita(sender, e);
    private void OnHomeFolderInfo(object? sender, RoutedEventArgs e) => Host?.OnHomeFolderInfo(sender, e);
    private void OnHomeKeys(object? sender, RoutedEventArgs e) => Host?.OnHomeKeys(sender, e);
    private void OnHomeOpenPkg(object? sender, RoutedEventArgs e) => Host?.OnHomeOpenPkg(sender, e);
    private void OnHomeOrganizer(object? sender, RoutedEventArgs e) => Host?.OnHomeOrganizer(sender, e);
    private void OnHomeResign(object? sender, RoutedEventArgs e) => Host?.OnHomeResign(sender, e);
    private void OnHomeScan(object? sender, RoutedEventArgs e) => Host?.OnHomeScan(sender, e);
    private void OnKeyLicenseAuditClick(object? sender, RoutedEventArgs e) => Host?.OnKeyLicenseAuditClick(sender, e);
    private void OnRecentPackageClick(object? sender, RoutedEventArgs e) => Host?.OnRecentPackageClick(sender, e);
}
