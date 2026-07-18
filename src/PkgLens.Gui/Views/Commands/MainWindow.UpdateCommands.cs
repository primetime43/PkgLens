using System;
using Avalonia.Interactivity;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async void OnCheckForUpdatesClick(object? sender, RoutedEventArgs e)
    {
        UpdateCheckResult? result = null;
        await RunOperationAsync("Checking GitHub for updates…", "Update check failed", async (token, _) =>
        {
            result = await GitHubUpdateChecker.Default.CheckAsync(GuiVersion.Value, token);
        });
        if (result is null)
            return;

        Vm.Status = !result.ReleaseFound
            ? "No published GitHub release was found."
            : result.UpdateAvailable
                ? $"PkgLens {result.LatestVersion} is available."
                : $"PkgLens {result.CurrentVersion} is the latest release.";
        await new UpdateCheckDialog(result).ShowDialog(this);
    }
}
