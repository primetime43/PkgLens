using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PkgLens.Core.Ps2;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async void OnExportPs2ClassicClick(object? sender, RoutedEventArgs e) =>
        await StartPs2ClassicExportAsync(Vm.Package?.FilePath);

    private async Task StartPs2ClassicExportAsync(string? sourcePath)
    {
        if (sourcePath is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the PS2 Classic package to export",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PS2 Classic package") { Patterns = new[] { "*.pkg" } },
                    FilePickerFileTypes.All,
                },
            });
            sourcePath = files.FirstOrDefault()?.TryGetLocalPath();
            if (sourcePath is null) return;
        }

        string inputPath = sourcePath;
        string? keysDirectory = Vm.KeysDirectory;
        string? rapDirectory = Vm.RapDirectory;
        Ps2ClassicExportEligibility? eligibility = null;
        await RunOperationAsync("Checking PS2 Classic package…", "PS2 Classic check failed", async (token, _) =>
        {
            eligibility = await Task.Run(() =>
            {
                using var package = File.OpenRead(inputPath);
                return Ps2ClassicPackageExporter.CheckEligibility(package,
                    new FileKeyProvider(keysDirectory), rapDirectory);
            }, token);
        });
        if (eligibility is null) return;
        if (!eligibility.CanExport)
        {
            Vm.ReportError("PS2 Classic export unavailable", new PkgLens.Core.PkgFormatException(eligibility.Reason));
            return;
        }
        if (!eligibility.RapAvailable && eligibility.ContentId is { } contentId)
        {
            await OfferNearbyRapsAsync(inputPath, new[] { contentId });
            using var package = File.OpenRead(inputPath);
            eligibility = Ps2ClassicPackageExporter.CheckEligibility(package,
                new FileKeyProvider(keysDirectory), rapDirectory);
        }
        if (!eligibility.RapAvailable)
        {
            Vm.ReportError("PS2 Classic RAP required", new PkgLens.Core.PkgKeyException(
                $"Import a valid {eligibility.ContentId}.rap into the RAP library, then try again."));
            return;
        }

        Ps2ClassicExportDialogResult? settings = await new Ps2ClassicExportDialog(inputPath, eligibility)
            .ShowDialog<Ps2ClassicExportDialogResult?>(this);
        if (settings is null) return;
        if (settings.RebuildCfwPackage)
            await OfferNearbyRapsForPathAsync(inputPath);
        var isoOutput = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save decrypted PS2 ISO as…",
            SuggestedFileName = Path.GetFileNameWithoutExtension(inputPath) + ".iso",
            DefaultExtension = "iso",
            FileTypeChoices = new[] { new FilePickerFileType("PS2 ISO") { Patterns = new[] { "*.iso" } } },
        });
        if (isoOutput?.TryGetLocalPath() is not { } isoPath) return;

        string? cfwPath = null;
        if (settings.RebuildCfwPackage)
        {
            var packageOutput = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the PS2 CFW/HEN package copy as…",
                SuggestedFileName = Path.GetFileNameWithoutExtension(inputPath) + "-ps2-cfw.pkg",
                DefaultExtension = "pkg",
                FileTypeChoices = new[] { new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } } },
            });
            cfwPath = packageOutput?.TryGetLocalPath();
            if (cfwPath is null) return;
        }

        string? finalCfwPath = cfwPath;
        string reportPath = Path.Combine(Path.GetDirectoryName(isoPath)!,
            Path.GetFileNameWithoutExtension(isoPath) + ".ps2-export.txt");
        if (!await ConfirmPreflightAsync("Checking PS2 Classic export readiness…", () =>
                OperationPreflight.Ps2ClassicExport(inputPath, isoPath,
                    new FileKeyProvider(keysDirectory), rapDirectory, settings.RebuildCfwPackage)))
            return;
        Ps2ClassicExportResult? result = null;
        await RunOperationAsync("Exporting PS2 Classic…", "PS2 Classic export failed", async (token, guiProgress) =>
        {
            var exportProgress = new Progress<Ps2ClassicProgress>(value =>
                guiProgress.Report(new GuiOperationProgress(value.Stage, value.Percentage)));
            await Task.Run(() =>
            {
                IKeyProvider keys = new FileKeyProvider(keysDirectory);
                AtomicOutput.EnsureDifferentPath(inputPath, isoPath);
                AtomicOutput.Write(isoPath, isoDestination =>
                {
                    using var package = File.OpenRead(inputPath);
                    result = Ps2ClassicPackageExporter.ExportIso(package, isoDestination, keys,
                        rapDirectory, cancellationToken: token, progress: exportProgress);
                });
                PostOperationVerifier.VerifyIso(isoPath, result!.IsoSize);

                if (finalCfwPath is not null)
                {
                    AtomicOutput.EnsureDifferentPath(inputPath, finalCfwPath);
                    AtomicOutput.Write(finalCfwPath, packageDestination =>
                    {
                        using var package = File.OpenRead(inputPath);
                        Ps2ClassicPackageExporter.RebuildCfwPackage(package, packageDestination, keys,
                            isoPath, finalCfwPath, rapDirectory, cancellationToken: token, progress: exportProgress);
                    });
                    PostOperationVerifier.VerifyPackage(finalCfwPath, keys);
                    using var rebuilt = File.OpenRead(finalCfwPath);
                    Ps2ClassicExportEligibility rebuiltEligibility = Ps2ClassicPackageExporter.CheckEligibility(
                        rebuilt, keys, rapDirectory);
                    if (!rebuiltEligibility.CanExport || !rebuiltEligibility.RapAvailable)
                        throw new PkgLens.Core.PkgFormatException("The rebuilt package's PS2 Classic image could not be reopened with the placeholder key.");
                    result = result! with { CfwPackagePath = finalCfwPath };
                }
            }, token);

            string report = Ps2ClassicPackageExporter.BuildReport(result!, inputPath, isoPath);
            await AtomicOutput.WriteAllTextAsync(reportPath, report, token);
            Vm.RefreshRapStatus();
            Vm.Status = finalCfwPath is null
                ? $"Exported and verified {Path.GetFileName(isoPath)}."
                : $"Exported ISO and verified CFW/HEN package {Path.GetFileName(finalCfwPath)}.";
            await new FileViewerDialog(Path.GetFileName(reportPath), Encoding.UTF8.GetBytes(report)).ShowDialog(this);
        });
    }
}
