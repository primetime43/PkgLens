using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PkgLens.Core.Ps1;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async void OnExportPs1ClassicClick(object? sender, RoutedEventArgs e) =>
        await StartPs1ClassicExportAsync(Vm.Package?.FilePath);

    internal async void OnHomeExportPs1(object? sender, RoutedEventArgs e) =>
        await StartPs1ClassicExportAsync(Vm.Package?.FilePath);

    private async Task StartPs1ClassicExportAsync(string? sourcePath)
    {
        if (sourcePath is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the PS1 Classic package to export",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PS1 Classic package") { Patterns = new[] { "*.pkg" } },
                    FilePickerFileTypes.All,
                },
            });
            sourcePath = files.FirstOrDefault()?.TryGetLocalPath();
            if (sourcePath is null) return;
        }

        string inputPath = sourcePath;
        string? keysDirectory = Vm.KeysDirectory;
        string? rapDirectory = Vm.RapDirectory;
        Ps1ClassicExportEligibility? eligibility = null;
        await RunOperationAsync("Checking PS1 Classic package…", "PS1 Classic check failed", async (token, _) =>
        {
            eligibility = await Task.Run(() =>
            {
                using var package = File.OpenRead(inputPath);
                return Ps1ClassicPackageExporter.CheckEligibility(package,
                    new FileKeyProvider(keysDirectory), rapDirectory);
            }, token);
        });
        if (eligibility is null) return;
        if (!eligibility.CanExport)
        {
            Vm.ReportError("PS1 Classic export unavailable", new PkgLens.Core.PkgFormatException(eligibility.Reason));
            return;
        }
        if (!eligibility.RapAvailable && eligibility.LicenseContentId is { } contentId)
        {
            await OfferNearbyRapsAsync(inputPath, new[] { contentId });
            using var package = File.OpenRead(inputPath);
            eligibility = Ps1ClassicPackageExporter.CheckEligibility(package,
                new FileKeyProvider(keysDirectory), rapDirectory);
        }
        if (!eligibility.RapAvailable)
        {
            Vm.ReportError("PS1 Classic RAP required", new PkgLens.Core.PkgKeyException(
                $"Import a valid {eligibility.LicenseContentId}.rap into the RAP library, then try again."));
            return;
        }

        Ps1ClassicExportDialogResult? settings =
            await new Ps1ClassicExportDialog(inputPath, eligibility).ShowDialog<Ps1ClassicExportDialogResult?>(this);
        if (settings is null) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the parent folder for the PS1 Classic export",
            AllowMultiple = false,
        });
        string? parentPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (parentPath is null) return;

        string? psxtractPath = null;
        if (settings.ReconstructBinCue)
        {
            psxtractPath = Ps1ToolSettings.Load();
            if (psxtractPath is null)
            {
                var tools = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select the maintained psxtract executable",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("psxtract") { Patterns = new[] { "psxtract.exe", "psxtract" } },
                        FilePickerFileTypes.All,
                    },
                });
                psxtractPath = tools.FirstOrDefault()?.TryGetLocalPath();
                if (psxtractPath is null) return;
                Ps1ToolSettings.Save(psxtractPath);
            }
        }

        string outputDirectory = CreateUniqueExportDirectory(parentPath,
            Path.GetFileNameWithoutExtension(inputPath) + "-ps1");
        string reconstructionDirectory = Path.Combine(outputDirectory, "disc-images");
        string reportPath = Path.Combine(outputDirectory, "ps1-export.txt");
        string? finalPsxtractPath = psxtractPath;
        await RunOperationAsync("Exporting PS1 Classic…", "PS1 Classic export failed", async (token, guiProgress) =>
        {
            var exportProgress = new Progress<Ps1ClassicExportProgress>(value =>
                guiProgress.Report(new GuiOperationProgress(value.Stage, value.Percentage)));
            Ps1ClassicPreparedExport prepared = await Task.Run(() =>
            {
                using var package = File.OpenRead(inputPath);
                return Ps1ClassicPackageExporter.Prepare(package, outputDirectory,
                    new FileKeyProvider(keysDirectory), rapDirectory,
                    cancellationToken: token, progress: exportProgress);
            }, token);

            Ps1ClassicReconstructionResult? reconstruction = null;
            if (finalPsxtractPath is not null)
                reconstruction = await Ps1ClassicPackageExporter.ReconstructAsync(prepared,
                    finalPsxtractPath, reconstructionDirectory, token, exportProgress);

            string report = Ps1ClassicPackageExporter.BuildReport(prepared, reconstruction, inputPath);
            await AtomicOutput.WriteAllTextAsync(reportPath, report, token);
            Vm.RefreshRapStatus();
            Vm.Status = reconstruction is null
                ? $"Exported PS1 Classic container and metadata to {Path.GetFileName(outputDirectory)}."
                : $"Exported and verified {reconstruction.BinFiles.Count} PS1 disc image(s).";
            await new FileViewerDialog(Path.GetFileName(reportPath), Encoding.UTF8.GetBytes(report)).ShowDialog(this);
        });
    }

    private static string CreateUniqueExportDirectory(string parentPath, string suggestedName)
    {
        string candidate = Path.Combine(parentPath, suggestedName);
        if (!Directory.Exists(candidate)) return candidate;
        for (int index = 2; ; index++)
        {
            candidate = Path.Combine(parentPath, $"{suggestedName}-{index}");
            if (!Directory.Exists(candidate)) return candidate;
        }
    }
}
