using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Primitives;
using System.Linq;
using PkgLens.Core.Ps3.Self;

namespace PkgLens.Gui.Views.Pages;

public partial class ResignPage : UserControl
{
    public ResignPage()
    {
        AvaloniaXamlLoader.Load(this);
        UpdateSigningProfiles();
        foreach (string name in new[] { "FselfNpdrmCheck", "SelfEncryptCheck", "SelfSignCheck" })
            this.FindControl<CheckBox>(name)!.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty) UpdateSigningProfiles();
            };
    }

    private void UpdateSigningProfiles()
    {
        bool npdrm = this.FindControl<CheckBox>("FselfNpdrmCheck")!.IsChecked == true;
        bool encrypted = this.FindControl<CheckBox>("SelfEncryptCheck")!.IsChecked == true;
        bool sign = this.FindControl<CheckBox>("SelfSignCheck")!.IsChecked == true;
        var box = this.FindControl<ComboBox>("SelfSigningProfileBox")!;
        ushort revision = (box.SelectedItem as LegacySelfProfile)?.Revision ?? 0x0A;
        var profiles = LegacySelfSigning.Profiles(npdrm);
        box.ItemsSource = profiles;
        box.SelectedItem = profiles.FirstOrDefault(p => p.Revision == revision)
            ?? profiles.First(p => p.Revision == 0x0A);
        this.FindControl<Grid>("SelfRevisionGrid")!.IsVisible = encrypted && !sign;
    }

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
