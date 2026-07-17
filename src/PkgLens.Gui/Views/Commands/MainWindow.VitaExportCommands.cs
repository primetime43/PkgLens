using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Vita;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async void OnExportVitaClick(object? sender, RoutedEventArgs e) =>
        await StartVitaExportAsync(Vm.Package?.FilePath);

    private async void OnHomeExportVita(object? sender, RoutedEventArgs e) =>
        await StartVitaExportAsync(null);

    private async Task StartVitaExportAsync(string? sourcePath)
    {
        if (sourcePath is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the PSVita package to export",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PSVita package") { Patterns = new[] { "*.pkg" } },
                    FilePickerFileTypes.All,
                },
            });
            sourcePath = files.FirstOrDefault()?.TryGetLocalPath();
            if (sourcePath is null) return;
        }

        string inputPath = sourcePath;
        string? keysDirectory = Vm.KeysDirectory;
        VitaPackageDetails? details = null;
        await RunOperationAsync("Checking PSVita package…", "Vita package check failed", async (token, _) =>
        {
            details = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var package = File.OpenRead(inputPath);
                return VitaPackageExporter.Inspect(package, new FileKeyProvider(keysDirectory));
            }, token);
        });
        if (details is null) return;

        VitaExportDialogResult? settings =
            await new VitaExportDialog(inputPath, details).ShowDialog<VitaExportDialogResult?>(this);
        if (settings is null) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the Vita output root (for example ux0)",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } destinationRoot) return;

        byte[]? workBin = settings.WorkBinPath is null ? null : await File.ReadAllBytesAsync(settings.WorkBinPath);
        if (!await ConfirmPreflightAsync("Checking PSVita export readiness…", () =>
                PkgLens.Core.Shared.OperationPreflight.VitaExport(inputPath, destinationRoot,
                    new FileKeyProvider(keysDirectory), workBin is not null)))
            return;

        VitaExportResult? result = null;
        await RunOperationAsync("Exporting PSVita package…", "Vita export failed", async (token, guiProgress) =>
        {
            var exportProgress = new Progress<VitaExportProgress>(value =>
                guiProgress.Report(new GuiOperationProgress(value.Stage, value.Percentage)));
            result = await Task.Run(() =>
            {
                using var package = File.OpenRead(inputPath);
                return VitaPackageExporter.Export(package, destinationRoot,
                    new FileKeyProvider(keysDirectory), workBin, token, exportProgress);
            }, token);

            string license = result.LicenseStatus == VitaLicenseStatus.Missing
                ? " License is still required for inner Vita content."
                : string.Empty;
            Vm.Status = $"Exported Vita {result.Package.Kind} → {result.OutputRoot}.{license}";
        });
    }
}
