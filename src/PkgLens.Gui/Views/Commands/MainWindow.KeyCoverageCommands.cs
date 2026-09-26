using System;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using PkgLens.Core.Shared;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    internal async void OnPackageKeyCoverage(object? sender, RoutedEventArgs e)
    {
        if (Vm.IsBusy || Vm.Package is not { IsDecrypted: true } package) return;
        do
        {
            // Capture UI-owned settings before entering the worker, including on rescan.
            var options = new ContentDecryptOptions { RapDirectory = Vm.RapDirectory };
            PackageKeyCoverageReport? report = null;
            await RunOperationAsync("Checking package key coverage…", "Key coverage scan failed", async (token, progress) =>
            {
                var scanProgress = new Progress<ContentDecryptProgress>(p => progress.Report(new GuiOperationProgress(p.Message, p.Percent)));
                report = await Task.Run(() => PackageKeyCoverageService.Scan(package.Operations,
                    options, token, scanProgress), token);
            });
            if (report is null) return;
            Vm.Status = report.Summary;
            if (!await new PackageKeyCoverageDialog(report).ShowDialog<bool>(this)) return;
        } while (Vm.Package == package && !Vm.IsBusy);
    }
}
