using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async void OnFirmwareAnalysisClick(object? sender, RoutedEventArgs e)
    {
        string? sourcePath = Vm.Package?.FilePath;
        if (sourcePath is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a package or SELF/SPRX to analyze",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Package / SELF") { Patterns = new[] { "*.pkg", "EBOOT.BIN", "*.self", "*.sprx" } },
                    FilePickerFileTypes.All,
                },
            });
            sourcePath = files.FirstOrDefault()?.TryGetLocalPath();
            if (sourcePath is null) return;
        }
        await StartFirmwareAnalysisAsync(sourcePath,
            Path.GetExtension(sourcePath).Equals(".pkg", StringComparison.OrdinalIgnoreCase));
    }

    private async void OnFirmwareFolderAnalysisClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an extracted game folder to analyze",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        await StartFirmwareAnalysisAsync(path, canPatchPackage: false);
    }

    private async Task StartFirmwareAnalysisAsync(string sourcePath, bool canPatchPackage)
    {
        await OfferNearbyRapsForPathAsync(sourcePath);
        string? keysDirectory = Vm.KeysDirectory;
        string? rapDirectory = Vm.RapDirectory;
        FirmwareAnalysisReport? report = null;
        await RunOperationAsync("Analyzing SELF/SPRX firmware…", "Firmware analysis failed", async (token, progress) =>
        {
            var firmwareProgress = new Progress<FirmwareAnalysisProgress>(value =>
                progress.Report(new GuiOperationProgress(
                    $"Analyzing {value.Path} ({value.Completed}/{value.Total})…",
                    value.Total == 0 ? 100 : value.Completed * 100d / value.Total)));
            report = await Task.Run(() => FirmwareAnalyzer.Analyze(sourcePath,
                new FileKeyProvider(keysDirectory),
                new FirmwareAnalysisOptions { RapDirectory = rapDirectory }, token, firmwareProgress), token);
        });
        if (report is null)
            return;

        Vm.Status = report.Items.Count == 0
            ? report.Guidance ?? "No PS3 executables were found for firmware analysis."
            : $"Firmware analysis: highest {report.HighestRequiredFirmware ?? "unknown"}; " +
              $"{report.PatchableCount}/{report.Items.Count} patchable.";
        FirmwarePatchRequest? request = await new FirmwareAnalysisDialog(report, canPatchPackage)
            .ShowDialog<FirmwarePatchRequest?>(this);
        if (request is not null)
            await StartCfwConversionAsync(sourcePath, new CfwConversionDialogResult(request.Target));
    }
}
