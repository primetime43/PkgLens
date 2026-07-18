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
    // ==================== Pack page ====================

    private string? _packFolder;

    private static readonly PkgContentType[] PackTypeChoices =
    {
        PkgContentType.GameExec, PkgContentType.GameData, PkgContentType.Theme,
        PkgContentType.Widget, PkgContentType.License, PkgContentType.Ps1Emu,
        PkgContentType.Psp, PkgContentType.Vsh, PkgContentType.Ps2Classic,
    };

    private void PopulatePackContentTypes()
    {
        var box = FindPageControl<ComboBox>("PackContentTypeBox")!;
        box.ItemsSource = PackTypeChoices.Select(t => $"{t} (0x{(uint)t:X})").ToList();
        box.SelectedIndex = 0; // GameExec
    }

    /// <summary>Menu/toolbar "Pack folder…" switches to the Pack page.</summary>
    private void OnGoPackClick(object? sender, RoutedEventArgs e) => Vm.ActiveTool = ToolPage.Pack;

    private string? _packRapPath;

    internal async void OnPackPickRap(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the RAP license file for the EBOOT's content id",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("RAP") { Patterns = new[] { "*.rap", "*.RAP" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        _packRapPath = path;
        FindPageControl<TextBlock>("PackRapText")!.Text = Path.GetFileName(path);
    }

    internal async void OnPackFolderInfo(object? sender, RoutedEventArgs e)
    {
        if (_packFolder is null)
        {
            Vm.Status = "Choose a source folder first.";
            return;
        }

        var box = FindPageControl<Border>("FolderInfoBox")!;
        var text = FindPageControl<TextBlock>("FolderInfoText")!;
        await RunOperationAsync("Inspecting folder…", "Folder inspection failed", async (token, _) =>
        {
            var report = await Task.Run(() => PkgLens.Core.Shared.GameFolderInfo.Describe(_packFolder), token);
            text.Text = PackagePresentationService.DescribeFolder(report);
            box.IsVisible = true;
            Vm.Status = $"Folder: {report.FileCount} file(s), {report.TotalBytes:n0} bytes.";
        });
    }

    internal void OnPackDrmChanged(object? sender, Avalonia.Controls.NumericUpDownValueChangedEventArgs e)
    {
        var label = FindPageControl<TextBlock>("PackDrmName");
        if (label is not null)
            label.Text = PkgLens.Core.Shared.Models.DrmType.Name((uint)(e.NewValue ?? 3));
    }

    internal async void OnPackBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the content folder to pack",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder)
            return;

        _packFolder = folder;
        FindPageControl<TextBlock>("PackFolderText")!.Text = folder;

        // Fast-Pack inference to pre-fill the fields (blank fallback if it can't infer).
        await RunOperationAsync("Inspecting package folder…", "Folder inference failed", async (token, _) =>
        {
            var plan = await Task.Run(() => PkgLens.Core.Shared.FolderPackage.Plan(folder), token);
            FindPageControl<TextBox>("PackContentIdBox")!.Text = plan.ContentId;
            FindPageControl<TextBox>("PackInstallDirBox")!.Text = plan.InstallDirectory;
            FindPageControl<NumericUpDown>("PackDrmBox")!.Value = plan.DrmType;
            SelectPackContentType(plan.ContentType);
            ShowPackNotes(plan.Notes.Count > 0 ? "Inferred: " + string.Join("; ", plan.Notes) : null);
            Vm.Status = $"Ready to pack {plan.FileCount} file(s) from {Path.GetFileName(folder)}.";
        });
    }

    internal async void OnPackBuild(object? sender, RoutedEventArgs e)
    {
        if (_packFolder is null)
        {
            Vm.Status = "Choose a source folder first.";
            return;
        }

        string contentId = (FindPageControl<TextBox>("PackContentIdBox")!.Text ?? string.Empty).Trim();
        if (contentId.Length == 0)
        {
            Vm.Status = "A content id is required (e.g. UP0001-NPUB30910_00-EXAMPLE000000001).";
            return;
        }

        string installDir = (FindPageControl<TextBox>("PackInstallDirBox")!.Text ?? string.Empty).Trim();
        bool retail = FindPageControl<RadioButton>("PackRetailRadio")!.IsChecked == true;
        bool resign = FindPageControl<CheckBox>("PackResignCheck")!.IsChecked == true;

        string? rapPath = _packRapPath;
        string? rapDirectory = Vm.RapDirectory;
        string? keysDirectory = Vm.KeysDirectory;
        if (resign && rapPath is null)
            await OfferNearbyRapsForPathAsync(_packFolder);

        var options = new PkgLens.Core.Shared.PackOptions
        {
            ContentId = contentId,
            InstallDirectory = installDir.Length == 0 ? null : installDir,
            ContentType = SelectedPackContentType(),
            DrmType = (uint)(FindPageControl<NumericUpDown>("PackDrmBox")!.Value ?? 3),
            Finalization = retail ? PkgFinalization.Retail : PkgFinalization.Debug,
            ResignEboot = resign,
            EbootKlicenseeResolver = resign
                ? id => RapLicenseService.ResolveContentId(id, rapPath, rapDirectory).Klicensee
                : null,
        };

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the new .pkg as…",
            SuggestedFileName = PackagePresentationService.SanitizeFileName(contentId) + ".pkg",
            DefaultExtension = "pkg",
            FileTypeChoices = new[] { new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } } },
        });
        if (file?.TryGetLocalPath() is not { } dest)
            return;

        string folder = _packFolder;
        await RunOperationAsync("Packing…", "Pack failed", async (token, progress) =>
        {
            var packageProgress = new Progress<PkgLens.Core.Shared.PkgOperationProgress>(value =>
                progress.Report(PackagePresentationService.ToGuiProgress(value)));
            var plan = await Task.Run(() =>
            {
                var p = PkgLens.Core.Shared.FolderPackage.Plan(folder, options, dest);
                IKeyProvider keys = new FileKeyProvider(keysDirectory);
                AtomicOutput.Write(dest, dst => p.Builder.Build(dst, keys, token, packageProgress));
                progress.Report(new GuiOperationProgress("Verifying rebuilt package…", null));
                PostOperationVerifier.VerifyPackage(dest, keys);
                return p;
            }, token);

            Vm.RefreshRapStatus();
            Vm.Status = retail
                ? $"Packed and verified {plan.FileCount} file(s) → {Path.GetFileName(dest)} — retail-encrypted (installs on CFW)."
                : $"Packed and verified {plan.FileCount} file(s) → {Path.GetFileName(dest)} — non-finalized (debug; RPCS3 / dev).";
            ShowPackNotes($"Wrote {new FileInfo(dest).Length:n0} bytes to {dest}");
        });
    }

    private void SelectPackContentType(uint value)
    {
        int idx = Array.FindIndex(PackTypeChoices, t => (uint)t == value);
        if (idx >= 0) FindPageControl<ComboBox>("PackContentTypeBox")!.SelectedIndex = idx;
    }

    private uint SelectedPackContentType()
    {
        int idx = FindPageControl<ComboBox>("PackContentTypeBox")!.SelectedIndex;
        return idx >= 0 ? (uint)PackTypeChoices[idx] : (uint)PkgContentType.GameExec;
    }

    private void ShowPackNotes(string? text)
    {
        var notes = FindPageControl<TextBlock>("PackNotes")!;
        notes.Text = text ?? string.Empty;
        notes.IsVisible = !string.IsNullOrEmpty(text);
    }
}
