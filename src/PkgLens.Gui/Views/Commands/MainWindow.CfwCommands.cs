using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async void OnConvertCfwClick(object? sender, RoutedEventArgs e) =>
        await StartCfwConversionAsync(Vm.Package?.FilePath);

    private async void OnHomeConvertCfw(object? sender, RoutedEventArgs e) =>
        await StartCfwConversionAsync(null);

    private async Task StartCfwConversionAsync(string? sourcePath,
        CfwConversionDialogResult? presetSettings = null)
    {
        if (sourcePath is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the PS3 package to convert",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } },
                    FilePickerFileTypes.All,
                },
            });
            sourcePath = files.FirstOrDefault()?.TryGetLocalPath();
            if (sourcePath is null) return;
        }

        CfwConversionDialogResult? settings = presetSettings ??
            await new CfwConversionDialog(sourcePath).ShowDialog<CfwConversionDialogResult?>(this);
        if (settings is null) return;

        string targetSuffix = settings.TargetProfile switch
        {
            TargetCompatibilityProfile.Rpcs3 => "rpcs3",
            TargetCompatibilityProfile.CexCfw => "cex-cfw",
            TargetCompatibilityProfile.Dex => "dex",
            TargetCompatibilityProfile.Hen => "hen",
            _ => "converted",
        };
        string suggested = Path.GetFileNameWithoutExtension(sourcePath) + $"-{targetSuffix}.pkg";
        var output = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save the {TargetCompatibilityAnalyzer.Describe(settings.TargetProfile).Name} package as…",
            SuggestedFileName = suggested,
            DefaultExtension = "pkg",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } },
            },
        });
        if (output?.TryGetLocalPath() is not { } destinationPath) return;

        string inputPath = sourcePath;
        string reportPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            Path.GetFileNameWithoutExtension(destinationPath) + ".report.txt");
        string? rapDirectory = Vm.RapDirectory;
        string? keysDirectory = Vm.KeysDirectory;
        await OfferNearbyRapsForPathAsync(inputPath);
        if (!await ConfirmPreflightAsync("Checking target compatibility…", () =>
                OperationPreflight.CfwConversion(inputPath, destinationPath,
                    new FileKeyProvider(keysDirectory), rapDirectory,
                    settings.TargetProfile, settings.FirmwareTarget), forceShow: true))
            return;

        CfwConversionReport? report = null;

        string targetName = TargetCompatibilityAnalyzer.Describe(settings.TargetProfile).Name;
        await RunOperationAsync($"Converting package for {targetName}…", "Package conversion failed", async (token, guiProgress) =>
        {
            var conversionProgress = new Progress<CfwConversionProgress>(value =>
                guiProgress.Report(new ViewModels.GuiOperationProgress(value.Stage, value.Percent)));
            await Task.Run(() =>
            {
                AtomicOutput.EnsureDifferentPath(inputPath, destinationPath);
                IKeyProvider keys = new FileKeyProvider(keysDirectory);
                var options = new CfwConversionOptions
                {
                    FirmwareTarget = settings.FirmwareTarget,
                    TargetProfile = settings.TargetProfile,
                    SourceName = inputPath,
                    OutputName = destinationPath,
                    KlicenseeResolver = contentId =>
                    {
                        RapLicenseResolution resolution =
                            RapLicenseService.ResolveContentId(contentId, null, rapDirectory);
                        return new CfwLicenseResolution(resolution.Klicensee, resolution.Source);
                    },
                };

                AtomicOutput.Write(destinationPath, destination =>
                {
                    using var source = File.OpenRead(inputPath);
                    report = CfwPackageConverter.Convert(source, destination, keys, options,
                        token, conversionProgress);
                });
                guiProgress.Report(new ViewModels.GuiOperationProgress("Verifying rebuilt package…", null));
                PostOperationVerifier.VerifyPackage(destinationPath, keys,
                    report!.Executables.Select(executable => executable.Path));
            }, token);

            string reportText = report!.ToText();
            await AtomicOutput.WriteAllTextAsync(reportPath, reportText, token);
            Vm.RefreshRapStatus();
            Vm.Status = $"Converted and verified for {TargetCompatibilityAnalyzer.Describe(report.TargetProfile).Name}: " +
                        $"{Path.GetFileName(destinationPath)} — {report.Executables.Count} executable(s); report: {Path.GetFileName(reportPath)}";
            await new FileViewerDialog(Path.GetFileName(reportPath), Encoding.UTF8.GetBytes(reportText)).ShowDialog(this);
        });
    }
}
