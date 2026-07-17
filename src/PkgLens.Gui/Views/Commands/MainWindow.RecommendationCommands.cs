using Avalonia.Controls;
using Avalonia.Interactivity;
using PkgLens.Core.Shared;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private void OnBrowsePackageClick(object? sender, RoutedEventArgs e) => Vm.ActiveTool = ViewModels.ToolPage.Package;

    private async void OnRecommendedActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PackageActionRecommendation recommendation } || Vm.Package is not { } package)
            return;

        switch (recommendation.Action)
        {
            case PackageRecommendedAction.ExportPsp:
                await StartPspExportAsync(package.FilePath);
                break;
            case PackageRecommendedAction.ExportVita:
                await StartVitaExportAsync(package.FilePath);
                break;
            case PackageRecommendedAction.ConvertCfw:
                await StartCfwConversionAsync(package.FilePath);
                break;
            case PackageRecommendedAction.ExtractAll:
                OnExtractAllClick(sender, e);
                break;
            case PackageRecommendedAction.Verify:
                OnVerifyClick(sender, e);
                break;
            case PackageRecommendedAction.KeyLicenseAudit:
                await new KeyLicenseAuditDialog(Vm.KeysDirectory, package.FilePath, autoStart: true).ShowDialog(this);
                break;
        }
    }
}
