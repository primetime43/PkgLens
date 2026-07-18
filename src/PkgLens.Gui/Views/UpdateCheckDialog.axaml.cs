using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class UpdateCheckDialog : Window
{
    public UpdateCheckDialog() : this(new UpdateCheckResult(
        "unknown", "unknown", new Uri("https://github.com/primetime43/PkgLens/releases"), false))
    {
    }

    public UpdateCheckDialog(UpdateCheckResult result)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("HeadingText")!.Text = !result.ReleaseFound
            ? "No published PkgLens release was found."
            : result.UpdateAvailable
                ? "A newer PkgLens release is available."
                : "PkgLens is up to date.";
        this.FindControl<TextBlock>("CurrentVersionText")!.Text = result.CurrentVersion;
        this.FindControl<TextBlock>("LatestVersionText")!.Text = result.LatestVersion ?? "None published";
        this.FindControl<TextBlock>("MessageText")!.Text = !result.ReleaseFound
            ? "GitHub does not currently list a stable release for this repository."
            : result.UpdateAvailable
                ? "Open the release page to download the build for your platform."
                : "You already have the latest published stable release.";
        this.FindControl<HyperlinkButton>("ReleaseLink")!.NavigateUri = result.ReleaseUri;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
