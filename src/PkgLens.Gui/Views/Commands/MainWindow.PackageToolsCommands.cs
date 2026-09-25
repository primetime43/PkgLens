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
    private void OnInfoClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is { } package)
            new PackageInfoDialog { DataContext = package }.ShowDialog(this);
    }

    private async void OnFolderInfoClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a content folder to inspect",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } dir)
            return;

        await RunOperationAsync("Inspecting folder…", "Folder inspection failed", async (token, _) =>
        {
            var report = await Task.Run(() => PkgLens.Core.Shared.GameFolderInfo.Describe(dir), token);
            await new FolderInfoDialog(dir, PackagePresentationService.DescribeFolder(report)).ShowDialog(this);
            Vm.Status = $"Folder: {report.FileCount} file(s), {report.TotalBytes:n0} bytes.";
        });
    }

    private void OnScanFolderClick(object? sender, RoutedEventArgs e) =>
        new ScanDialog(Vm.KeysDirectory).ShowDialog(this);

    internal void OnDecryptManual(object? sender, RoutedEventArgs e) => ShowManual();

    internal void OnExploreTrophies(object? sender, RoutedEventArgs e) => ShowTrophies();

    /// <summary>Parses the selected TROPHY.TRP and opens its metadata and artwork browser.</summary>
    private async void ShowTrophies()
    {
        if (Vm.Package is not { SelectedIsTrophyTrp: true } package)
        {
            Vm.Status = "Select a TROPHY.TRP file first.";
            return;
        }

        await RunOperationAsync("Reading trophy set…", "Trophy archive could not be opened", async (token, _) =>
        {
            var set = await Task.Run(() => package.Operations.ReadTrophySet(package.SelectedItem!.Entry!), token);
            Vm.Status = $"{set.Name}: {set.Trophies.Count} trophies.";
            await new TrophyExplorerDialog(set).ShowDialog(this);
        });
    }

    /// <summary>Decrypts the selected DOCUMENT.DAT and opens its manual pages in a viewer.</summary>
    private async void ShowManual()
    {
        if (Vm.Package is not { SelectedIsDocument: true } package)
        {
            Vm.Status = "Select a DOCUMENT.DAT file first.";
            return;
        }

        string name = package.SelectedItem?.Name ?? "DOCUMENT.DAT";
        await RunOperationAsync("Decrypting manual…", "Manual decrypt failed", async (token, _) =>
        {
            var pages = await Task.Run(() => package.Operations.DecryptDocument(package.SelectedItem!.Entry!), token);
            if (pages.Count == 0)
            {
                Vm.Status = "No manual pages were found in this DOCUMENT.DAT.";
                return;
            }
            Vm.Status = $"Decrypted {pages.Count} manual page(s).";
            await new ManualViewerDialog(name, pages).ShowDialog(this);
        });
    }

    internal async void OnUnpackPbp(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { SelectedIsPbp: true } package)
        {
            Vm.Status = "Select a .PBP file first.";
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Unpack the PBP into…",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } dir)
            return;

        await RunOperationAsync("Unpacking PBP…", "PBP unpack failed", async (token, _) =>
        {
            var written = await Task.Run(() => package.Operations.UnpackPbp(package.SelectedItem!.Entry!, dir, token), token);
            Vm.Status = $"Unpacked {written.Count} section(s) → {dir}";
        });
    }

    internal async void OnExtractPspIso(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { SelectedIsPbp: true } package)
        {
            Vm.Status = "Select an EBOOT.PBP file first.";
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(package.SelectedItem?.Name ?? "GAME");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the decrypted PSP ISO as…",
            SuggestedFileName = (package.TitleId is { Length: > 0 } tid ? tid : baseName) + ".iso",
            DefaultExtension = "iso",
            FileTypeChoices = new[] { new FilePickerFileType("PSP ISO") { Patterns = new[] { "*.iso" } } },
        });
        if (file?.TryGetLocalPath() is not { } dest)
            return;

        await RunOperationAsync("Decrypting PSP ISO…", "ISO extraction failed", async (token, progress) =>
        {
            var isoProgress = new Progress<double>(percent =>
                progress.Report(new GuiOperationProgress("Decrypting PSP ISO…", percent)));
            await Task.Run(() =>
            {
                package.Operations.ExportPspIso(package.SelectedItem!.Entry!, dest, token, isoProgress);
                progress.Report(new GuiOperationProgress("Verifying exported ISO…", null));
                PostOperationVerifier.VerifyIso(dest);
            }, token);
            Vm.Status = $"Saved and verified PSP ISO → {Path.GetFileName(dest)} — ready to run in a PSP emulator " +
                        "(the EBOOT/.prx executables inside stay encrypted until the emulator loads them).";
        });
    }

    private void OnEditSfoClick(object? sender, RoutedEventArgs e) => EditSfo();

    private async void EditSfo()
    {
        try
        {
            if (Vm.Package is not { Sfo: { } sfo } package)
                return;
            var edited = await new SfoEditorDialog(sfo.Entries).ShowDialog<List<PkgLens.Core.Shared.Sfo.SfoEntry>?>(this);
            if (edited is null)
                return;
            package.Operations.ApplySfoEdits(edited);
            Vm.Status = $"PARAM.SFO edited — {package.PendingChangesSummary}. Review changes or save a copy.";
        }
        catch (Exception ex)
        {
            Vm.ReportError("SFO edit failed", ex);
        }
    }

    private async void OnVerifyClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { } package)
            return;
        await RunOperationAsync("Verifying package…", "Verify failed", async (token, _) =>
        {
            var report = await Task.Run(package.Operations.Verify, token);
            await new VerifyDialog(report).ShowDialog(this);
        });
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e) =>
        new AboutDialog().ShowDialog(this);

    internal async void OnReplaceClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { SelectedItem: { IsDirectory: false, Entry: not null } node } package)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replace {node.Name} with…",
            AllowMultiple = false,
            FileTypeFilter = PackagePresentationService.ReplacementFilters(node.Name),
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        await RunOperationAsync($"Reading {Path.GetFileName(path)}…", "Replace failed", async (token, _) =>
        {
            byte[] content = await File.ReadAllBytesAsync(path, token);
            package.Operations.ReplaceEntry(node.Entry!, content);
            Vm.Status = $"Replaced {node.Name} ({EntryNode.FormatSize(node.Size)} → " +
                         $"{EntryNode.FormatSize((ulong)content.Length)}). {package.PendingChangeCount} pending change(s) — " +
                         "review changes or save a copy.";
        });
    }

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e) => await SavePackageCopyAsync();

    private async Task<bool> SavePackageCopyAsync()
    {
        if (Vm.IsBusy || Vm.Package is not { } package)
            return false;

        PackageSelfSelection? executableSelection = null;
        if (package.Operations.Info.Header.Platform == PkgLens.Core.Shared.Models.PkgPlatform.Ps3)
        {
            executableSelection = await new PackageSelfDialog(Vm.RapDirectory, 0,
                (settings, token, progress) => PreparedPackageExecutables.ForPackage(package.Operations, settings, token, progress))
                .ShowDialog<PackageSelfSelection?>(this);
            if (executableSelection is null) return false;
        }
        using var executables = executableSelection?.Prepared;
        string suggested = System.IO.Path.GetFileNameWithoutExtension(package.FilePath) + "-modified.pkg";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save repacked .pkg as…",
            SuggestedFileName = suggested,
            DefaultExtension = "pkg",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } },
            },
        });
        if (file?.TryGetLocalPath() is not { } dest)
            return false;
        return await Vm.SavePackageAsAsync(dest, executables);
    }

    private async void OnCloseClick(object? sender, RoutedEventArgs e) => await Vm.CloseFileAsync();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
