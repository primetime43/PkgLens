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
    internal async void OnRecentPackageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecentPackageItem recent })
            await Vm.LoadRecentAsync(recent);
    }

    internal void OnClearRecentPackages(object? sender, RoutedEventArgs e) => Vm.ClearRecentPackages();

    internal void OnClearFileFilter(object? sender, RoutedEventArgs e) => Vm.Package?.ClearFileFilter();

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PS3 .pkg",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } },
                FilePickerFileTypes.All,
            },
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await Vm.LoadAsync(path);
    }

    internal async void OnSetKeyClick(object? sender, RoutedEventArgs e)
    {
        var key = await new KeyImportDialog().ShowDialog<byte[]?>(this);
        if (key is null)
            return;

        try
        {
            string path = KeyStore.Install(key);
            Vm.Status = $"Key saved to {path} — {KeyStore.Describe(key)}";
            Vm.RefreshKeyStatus();
            if (Vm.Package is { } current)
                await Vm.LoadAsync(current.FilePath);
        }
        catch (Exception ex)
        {
            Vm.ReportError("Could not save key", ex);
        }
    }

    internal async void OnKeysClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select keys folder",
            AllowMultiple = false,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } dir)
        {
            Vm.KeysDirectory = dir;
            if (Vm.Package is { } current)
                await Vm.LoadAsync(current.FilePath);
        }
    }

    internal async void OnManageRapsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new RapManagerDialog(Vm.RapDirectory);
        await dialog.ShowDialog(this);
        Vm.RapDirectory = dialog.SelectedDirectory;
        Vm.RefreshRapStatus();
    }

    internal async void OnRapSearchFoldersClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folders that may contain RAP licenses",
            AllowMultiple = true,
        });
        string[] paths = folders.Select(folder => folder.TryGetLocalPath())
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();
        if (paths.Length > 0)
            Vm.SetRapSearchDirectories(paths);
    }

    internal void OnClearRapSearchFoldersClick(object? sender, RoutedEventArgs e) =>
        Vm.SetRapSearchDirectories(Array.Empty<string>());

    internal async void OnExtractClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { SelectedItem: { IsDirectory: false, Entry: not null } node } package)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Extract file to…",
            SuggestedFileName = node.Name,
        });

        if (file?.TryGetLocalPath() is not { } dest)
            return;

        await RunOperationAsync($"Extracting {node.Name}…", "Extract failed", async (token, progress) =>
        {
            var byteProgress = new Progress<long>(bytes =>
                progress.Report(new GuiOperationProgress($"Extracting {node.Name}…",
                    node.Size == 0 ? 100 : bytes * 100d / node.Size)));
            await Task.Run(() => package.Operations.ExtractEntry(node.Entry!, dest, token, byteProgress), token);
            Vm.Status = $"Extracted {node.Name} → {dest}";
        });
    }

    // Right-click selects the row under the cursor so the context menu acts on it.
    internal void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed &&
            (e.Source as Control)?.DataContext is EntryNode node &&
            Vm.Package is { } package)
        {
            package.SelectedItem = node;
        }
    }

    private async void OnExtractAllClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { IsDecrypted: true } package)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Extract all — choose where to create the package folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } dir)
            return;

        // Extract into a subfolder named after the package, so files don't spill into the chosen dir.
        string dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(package.FilePath));

        await RunOperationAsync("Extracting all files…", "Extract all failed", async (token, progress) =>
        {
            var packageProgress = new Progress<PkgLens.Core.Shared.PkgOperationProgress>(value =>
                progress.Report(PackagePresentationService.ToGuiProgress(value)));
            int n = await Task.Run(() => package.Operations.ExtractAll(dest, token, packageProgress), token);
            Vm.Status = $"Extracted {n} file(s) to {dest}";
        });
    }

    internal void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm.Package?.SelectedItem is not { } node)
            return;

        if (node.IsDirectory)
            Vm.Package.OpenFolder(node);
        else if (node.Name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) && Vm.Package.CanEditSfo)
            EditSfo();   // SFO opens in the editor, not the hex viewer
        else if (Vm.Package.SelectedIsDocument)
            ShowManual();   // DOCUMENT.DAT opens as its decrypted manual pages
        else if (Vm.Package.SelectedIsTrophyTrp)
            ShowTrophies(); // TROPHY.TRP opens as its trophy list and artwork
        else
            ViewSelected();
    }

    internal void OnViewClick(object? sender, RoutedEventArgs e) => ViewSelected();

    private async void ViewSelected()
    {
        if (Vm.Package is not { SelectedItem: { IsDirectory: false, Entry: not null } node } package)
            return;

        if (node.Size > PackageViewModel.MaxPreviewBytes)
        {
            Vm.Status = $"{node.Name} is too large to preview ({EntryNode.FormatSize(node.Size)}). Use Extract instead.";
            return;
        }

        byte[]? data = null;
        await RunOperationAsync($"Reading {node.Name}…", $"Could not read {node.Name}", async (token, _) =>
        {
            data = await Task.Run(() => package.Operations.ReadEntryBytes(node.Entry!), token);
        });
        if (data is null)
            return;

        string title = node.Name;
        // If it's an EDAT/SDAT (PS3 NPDRM or PSP EDAT/PGD), decrypt it so the viewer shows the real contents.
        if (PkgLens.Core.Ps3.Npd.EdatFile.IsEdat(data) || PkgLens.Core.Psp.PspEdatFile.IsPspEncrypted(data))
            (data, title) = await DecryptEdatForView(data, node.Name, package.FilePath);

        await new FileViewerDialog(title, data).ShowDialog(this);
    }

    /// <summary>Decrypts an EDAT/SDAT for viewing (SDAT/free automatic; licensed resolves a RAP).</summary>
    private async Task<(byte[] data, string title)> DecryptEdatForView(
        byte[] data, string name, string packagePath)
    {
        // PSP EDAT ("\0PSPEDAT") / bare PGD ("\0PGD") decrypt via the PSP path (fixed key, no RAP).
        if (PkgLens.Core.Psp.PspEdatFile.IsPspEncrypted(data))
        {
            try
            {
                byte[] pspPlain = await Task.Run(() => PkgLens.Core.Psp.PspEdatFile.DecryptToArray(new MemoryStream(data)));
                Vm.Status = $"Decrypted PSP EDAT ({data.Length:n0} → {pspPlain.Length:n0} bytes)";
                return (pspPlain, $"{name}  ·  decrypted PSP EDAT");
            }
            catch (Exception ex)
            {
                Vm.ReportError("PSP EDAT decrypt failed", ex);
                return (data, name);
            }
        }

        var npd = PkgLens.Core.Ps3.Npd.EdatFile.ParseHeader(new MemoryStream(data));

        byte[]? klic = null;
        if (npd.NeedsKlicensee)
        {
            await OfferNearbyRapsAsync(packagePath, new[] { npd.ContentId });
            byte[]? rap = PkgLens.Core.Ps3.Npd.RapStore.Find(npd.ContentId, Vm.RapDirectory) ??
                          await PromptForRap(npd.ContentId);
            if (rap is null)
            {
                Vm.Status = $"{npd.ContentId}: licensed EDAT — no RAP provided, showing the raw encrypted file.";
                return (data, name);
            }
            klic = PkgLens.Core.Ps3.Npd.NpdKeys.RapToKlicensee(rap);
        }

        try
        {
            byte[] plain = PkgLens.Core.Ps3.Npd.EdatFile.DecryptToArray(new MemoryStream(data), klic);
            Vm.Status = $"Decrypted EDAT: {npd.ContentId} (DRM {npd.LicenseText})";
            return (plain, $"{name}  ·  decrypted EDAT");
        }
        catch (Exception ex)
        {
            Vm.ReportError("EDAT decrypt failed", ex);
            return (data, name);
        }
    }

    private async Task<byte[]?> PromptForRap(string contentId)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select the RAP for {contentId}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("RAP license") { Patterns = new[] { "*.rap" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return null;

        var rap = await File.ReadAllBytesAsync(path);
        if (rap.Length != 16)
        {
            Vm.Status = "That file is not a 16-byte RAP.";
            return null;
        }
        try
        {
            PkgLens.Core.Ps3.Npd.RapStore.Install(contentId, rap, Vm.RapDirectory);
            Vm.RefreshRapStatus();
        }
        catch { /* best-effort caching */ }
        return rap;
    }
}
