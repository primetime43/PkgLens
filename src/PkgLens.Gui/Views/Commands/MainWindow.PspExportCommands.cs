using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async void OnExportPspClick(object? sender, RoutedEventArgs e) =>
        await StartPspExportAsync(Vm.Package?.FilePath);

    private async void OnHomeExportPsp(object? sender, RoutedEventArgs e) =>
        await StartPspExportAsync(null);

    private async Task StartPspExportAsync(string? sourcePath)
    {
        PspExportEligibility? eligibility = null;
        if (sourcePath is not null && Vm.Package is { } openPackage)
        {
            eligibility = openPackage.PspExportEligibility;
            if (!eligibility.CanExport)
            {
                Vm.ReportError("PSP export unavailable", new PkgLens.Core.PkgFormatException(eligibility.Reason));
                return;
            }
        }

        if (sourcePath is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the PSP package to export",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PSP package") { Patterns = new[] { "*.pkg" } },
                    FilePickerFileTypes.All,
                },
            });
            sourcePath = files.FirstOrDefault()?.TryGetLocalPath();
            if (sourcePath is null) return;
        }

        if (eligibility is null)
        {
            string candidatePath = sourcePath;
            string? candidateKeysDirectory = Vm.KeysDirectory;
            await RunOperationAsync("Checking PSP package…", "Package check failed", async (token, _) =>
            {
                eligibility = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    using var package = File.OpenRead(candidatePath);
                    return PspPackageExporter.CheckEligibility(package, new FileKeyProvider(candidateKeysDirectory));
                }, token);
            });
            if (eligibility is null) return;
            if (!eligibility.CanExport)
            {
                Vm.ReportError("PSP export unavailable", new PkgLens.Core.PkgFormatException(eligibility.Reason));
                return;
            }
        }

        PspExportFormat? format = await new PspExportDialog(sourcePath).ShowDialog<PspExportFormat?>(this);
        if (format is null) return;

        string extension = format.Value.ToString().ToLowerInvariant();
        string suggestedName = format == PspExportFormat.Pbp
            ? "EBOOT.PBP"
            : Path.GetFileNameWithoutExtension(sourcePath) + "." + extension;
        var output = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save PSP {format.Value.ToString().ToUpperInvariant()} as…",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType($"PSP {format.Value.ToString().ToUpperInvariant()}")
                {
                    Patterns = new[] { "*." + extension },
                },
            },
        });
        if (output?.TryGetLocalPath() is not { } destinationPath) return;

        string inputPath = sourcePath;
        string? keysDirectory = Vm.KeysDirectory;
        PspExportResult? result = null;
        await RunOperationAsync("Exporting PSP package…", "PSP export failed", async (token, guiProgress) =>
        {
            var exportProgress = new Progress<PspExportProgress>(value =>
                guiProgress.Report(new GuiOperationProgress(value.Stage, value.Percentage)));
            await Task.Run(() =>
            {
                AtomicOutput.EnsureDifferentPath(inputPath, destinationPath);
                IKeyProvider keys = new FileKeyProvider(keysDirectory);
                AtomicOutput.Write(destinationPath, destination =>
                {
                    using var package = File.OpenRead(inputPath);
                    result = PspPackageExporter.Export(package, destination, keys, format.Value,
                        cancellationToken: token, progress: exportProgress);
                });
            }, token);

            string detail = result!.DiscId is { Length: > 0 } discId ? $" · disc {discId}" : string.Empty;
            Vm.Status = $"Exported {Path.GetFileName(destinationPath)} ({result.OutputSize:n0} bytes){detail}.";
        });
    }
}
