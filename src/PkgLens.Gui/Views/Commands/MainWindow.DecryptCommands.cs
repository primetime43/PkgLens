using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    // ==================== Decrypt page ====================

    private string? _decryptFile;
    private string? _decryptRap;
    private string? _decryptResultPath;

    /// <summary>Shows or hides the "View result" button and remembers the file it points at.</summary>
    private void SetDecryptResult(string? path)
    {
        _decryptResultPath = path;
        FindPageControl<Button>("DecryptViewButton")!.IsVisible = path is not null;
    }

    internal async void OnDecryptBrowseFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an EDAT / SDAT file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("EDAT / SDAT") { Patterns = new[] { "*.edat", "*.sdat" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        _decryptFile = path;
        FindPageControl<TextBlock>("DecryptFileText")!.Text = Path.GetFileName(path);
        HideResultBanner("DecryptBanner");
        SetDecryptResult(null); // a new input invalidates any previous result
    }

    internal async void OnDecryptBrowseRap(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the RAP license",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("RAP license") { Patterns = new[] { "*.rap" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        _decryptRap = path;
        FindPageControl<TextBlock>("DecryptRapText")!.Text = Path.GetFileName(path);
    }

    internal async void OnDecryptRun(object? sender, RoutedEventArgs e)
    {
        if (_decryptFile is null)
        {
            Vm.Status = "Choose an EDAT/SDAT file first.";
            return;
        }

        string sourcePath = _decryptFile;
        var save = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save decrypted file as…",
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath),
        });
        if (save?.TryGetLocalPath() is not { } destinationPath)
            return;

        string? rapDirectory = Vm.RapDirectory;
        if (_decryptRap is null)
            await OfferNearbyRapsForPathAsync(sourcePath);
        if (!await ConfirmPreflightAsync("Checking decrypt readiness…", () =>
                PkgLens.Core.Shared.OperationPreflight.DataDecrypt(sourcePath, destinationPath,
                    _decryptRap, rapDirectory)))
            return;

        await RunOperationAsync("Decrypting data file…", "Decrypt failed", async (token, guiProgress) =>
        {
            string summary = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var input = File.OpenRead(sourcePath);
                if (PkgLens.Core.Psp.PspEdatFile.IsPspEncrypted(input))
                {
                    AtomicOutput.EnsureDifferentPath(sourcePath, destinationPath);
                    AtomicOutput.Write(destinationPath,
                        output => PkgLens.Core.Psp.PspEdatFile.Decrypt(input, output));
                    guiProgress.Report(new GuiOperationProgress("Verifying decrypted output…", null));
                    PostOperationVerifier.VerifyDecryptedData(sourcePath, destinationPath,
                        cancellationToken: token);
                    return $"Decrypted and verified PSP EDAT — wrote {new FileInfo(destinationPath).Length:n0} bytes.";
                }

                var npd = PkgLens.Core.Ps3.Npd.EdatFile.ParseHeader(input);
                byte[]? klicensee = null;
                if (npd.NeedsKlicensee)
                {
                    RapLicenseResolution resolution = RapLicenseService.ResolveContentId(
                        npd.ContentId, _decryptRap, rapDirectory);
                    klicensee = resolution.Klicensee ?? throw new PkgLens.Core.PkgKeyException(
                        $"No valid RAP is available for {npd.ContentId}.");
                }

                input.Position = 0;
                AtomicOutput.EnsureDifferentPath(sourcePath, destinationPath);
                AtomicOutput.Write(destinationPath,
                    output => PkgLens.Core.Ps3.Npd.EdatFile.Decrypt(input, output, klicensee));
                guiProgress.Report(new GuiOperationProgress("Verifying decrypted output…", null));
                PostOperationVerifier.VerifyDecryptedData(sourcePath, destinationPath, klicensee, token);
                return $"Decrypted and verified {npd.ContentId} — wrote {new FileInfo(destinationPath).Length:n0} bytes.";
            }, token);

            Vm.RefreshRapStatus();
            Vm.Status = summary;
            ShowResultBanner("DecryptBanner", ok: true, summary, destinationPath);
            SetDecryptResult(destinationPath);
        });
    }

    internal async void OnDecryptView(object? sender, RoutedEventArgs e)
    {
        if (_decryptResultPath is not { } path || !File.Exists(path))
        {
            Vm.Status = "No decrypted file to view — run Decrypt first.";
            return;
        }
        await RunOperationAsync("Opening decrypted file…", "Could not open the decrypted file", async (token, _) =>
        {
            byte[] data = await File.ReadAllBytesAsync(path, token);
            await new FileViewerDialog(Path.GetFileName(path), data).ShowDialog(this);
        });
    }
}
