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
        this.FindControl<Button>("DecryptViewButton")!.IsVisible = path is not null;
    }

    private async void OnDecryptBrowseFile(object? sender, RoutedEventArgs e)
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
        this.FindControl<TextBlock>("DecryptFileText")!.Text = Path.GetFileName(path);
        HideResultBanner("DecryptBanner");
        SetDecryptResult(null); // a new input invalidates any previous result
    }

    private async void OnDecryptBrowseRap(object? sender, RoutedEventArgs e)
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
        this.FindControl<TextBlock>("DecryptRapText")!.Text = Path.GetFileName(path);
    }

    private async void OnDecryptRun(object? sender, RoutedEventArgs e)
    {
        if (_decryptFile is null)
        {
            Vm.Status = "Choose an EDAT/SDAT file first.";
            return;
        }

        await RunOperationAsync("Decrypting data file…", "Decrypt failed", async (token, _) =>
        {
            byte[] bytes = await File.ReadAllBytesAsync(_decryptFile, token);

            // PSP EDAT / bare PGD: decrypt via the PSP path (fixed key, no RAP).
            if (PkgLens.Core.Psp.PspEdatFile.IsPspEncrypted(bytes))
            {
                var pspSave = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save decrypted PSP EDAT as…",
                    SuggestedFileName = Path.GetFileNameWithoutExtension(_decryptFile),
                });
                if (pspSave?.TryGetLocalPath() is not { } pspDest)
                    return;
                string pspSrc = _decryptFile;
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    AtomicOutput.EnsureDifferentPath(pspSrc, pspDest);
                    using var input = File.OpenRead(pspSrc);
                    AtomicOutput.Write(pspDest,
                        output => PkgLens.Core.Psp.PspEdatFile.Decrypt(input, output));
                }, token);
                Vm.Status = $"Decrypted PSP EDAT → {Path.GetFileName(pspDest)}";
                ShowResultBanner("DecryptBanner", ok: true,
                    $"Decrypted PSP EDAT — wrote {new FileInfo(pspDest).Length:n0} bytes.", pspDest);
                SetDecryptResult(pspDest);
                return;
            }

            var npd = PkgLens.Core.Ps3.Npd.EdatFile.ParseHeader(new MemoryStream(bytes));

            byte[]? klic = null;
            if (npd.NeedsKlicensee)
            {
                RapLicenseResolution resolution = RapLicenseService.ResolveContentId(
                    npd.ContentId, _decryptRap, Vm.RapDirectory);
                if (resolution.Klicensee is null)
                {
                    Vm.Status = $"{npd.ContentId} is a licensed EDAT — import its RAP in Keys → RAP Library, or browse to it here.";
                    return;
                }
                klic = resolution.Klicensee;
                Vm.RefreshRapStatus();
            }

            var save = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save decrypted file as…",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_decryptFile),
            });
            if (save?.TryGetLocalPath() is not { } dest)
                return;

            string src = _decryptFile;
            byte[]? k = klic;
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                AtomicOutput.EnsureDifferentPath(src, dest);
                using var input = File.OpenRead(src);
                AtomicOutput.Write(dest,
                    output => PkgLens.Core.Ps3.Npd.EdatFile.Decrypt(input, output, k));
            }, token);
            Vm.Status = $"Decrypted {npd.ContentId} → {Path.GetFileName(dest)}";
            ShowResultBanner("DecryptBanner", ok: true,
                $"Decrypted {npd.ContentId} — wrote {new FileInfo(dest).Length:n0} bytes.", dest);
            SetDecryptResult(dest);
        });
    }

    private async void OnDecryptView(object? sender, RoutedEventArgs e)
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
