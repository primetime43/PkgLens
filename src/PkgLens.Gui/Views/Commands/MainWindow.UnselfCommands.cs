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
    private string? _unselfRapPath;

    internal async void OnPickRap(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the RAP license file for this content id",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("RAP") { Patterns = new[] { "*.rap", "*.RAP" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        _unselfRapPath = path;
        FindPageControl<TextBlock>("UnselfRapText")!.Text = Path.GetFileName(path);
    }

    internal async void OnUnself(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an encrypted EBOOT.BIN / SELF to decrypt",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SELF / EBOOT") { Patterns = new[] { "EBOOT.BIN", "*.self", "*.sprx", "*.bin" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } input)
            return;

        bool chain = FindPageControl<CheckBox>("UnselfThenResignCheck")!.IsChecked == true;
        string? rap = _unselfRapPath;

        string baseName = Path.GetFileNameWithoutExtension(input);
        var save = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = chain ? "Save the fake-signed SELF as…" : "Save the decrypted ELF as…",
            SuggestedFileName = chain ? "EBOOT.BIN" : baseName + ".ELF",
            DefaultExtension = chain ? "BIN" : "ELF",
        });
        if (save?.TryGetLocalPath() is not { } dest)
            return;

        string? rapDirectory = Vm.RapDirectory;
        if (rap is null)
            await OfferNearbyRapsForPathAsync(input);
        if (!await ConfirmPreflightAsync("Checking SELF decrypt readiness…", () =>
                PkgLens.Core.Shared.OperationPreflight.SelfDecrypt(input, dest, rap,
                    rapDirectory, chain)))
            return;

        await RunOperationAsync("Decrypting SELF…", "SELF decrypt failed", async (token, _) =>
        {
            var summary = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                byte[] self = File.ReadAllBytes(input);
                RapLicenseResolution resolution = RapLicenseService.ResolveSelf(self, rap, rapDirectory);
                var result = PkgLens.Core.Ps3.Self.SelfDecryptor.Decrypt(self, resolution.Klicensee);
                string lic = result.WasNpdrm ? (result.License?.ToString() ?? "NPDRM") : "non-NPDRM";
                string source = resolution.Source is null ? string.Empty : $", {resolution.Source}";

                if (chain)
                {
                    byte[] fself = PkgLens.Core.Ps3.Self.SelfBuilder.MakeFakeSelf(result.Elf, npdrm: result.WasNpdrm);
                    AtomicOutput.EnsureDifferentPath(input, dest);
                    AtomicOutput.WriteAllBytes(dest, fself);
                    PostOperationVerifier.VerifyFakeSelf(dest, result.Elf);
                    // Drop the intermediate ELF beside the fSELF for reference.
                    string elfBeside = Path.Combine(Path.GetDirectoryName(dest) ?? "", baseName + ".ELF");
                    AtomicOutput.EnsureDifferentPath(input, elfBeside);
                    AtomicOutput.WriteAllBytes(elfBeside, result.Elf);
                    return $"Decrypted ({lic}{source}) → verified fake-signed fSELF {Path.GetFileName(dest)} ({fself.Length:n0} bytes); ELF beside it.";
                }

                AtomicOutput.EnsureDifferentPath(input, dest);
                AtomicOutput.WriteAllBytes(dest, result.Elf);
                return $"Decrypted {self.Length:n0}-byte SELF ({lic}{source}) → {Path.GetFileName(dest)} ({result.Elf.Length:n0} bytes).";
            }, token);
            Vm.RefreshRapStatus();
            Vm.Status = summary;
            ShowResultBanner("UnselfBanner", ok: true, summary, dest);
        });
    }

    /// <summary>
    /// Fills a prominent result banner (named "{banner}", with "{banner}Icon/Title/Path/Show" parts)
    /// in the main content area. <paramref name="path"/> non-null shows the output path and a
    /// "Show in folder" button; a failure (<paramref name="ok"/> = false) styles the banner red.
    /// </summary>
    private void ShowResultBanner(string banner, bool ok, string title, string? path)
    {
        var box = FindPageControl<Border>(banner)!;
        var icon = FindPageControl<TextBlock>($"{banner}Icon")!;
        var titleText = FindPageControl<TextBlock>($"{banner}Title")!;
        var pathText = FindPageControl<SelectableTextBlock>($"{banner}Path")!;
        var show = FindPageControl<Button>($"{banner}Show")!;

        box.Classes.Set("error", !ok);
        icon.Text = ok ? "✓" : "✕"; // ✓ / ✕
        titleText.Text = title;
        pathText.Text = path ?? string.Empty;
        pathText.IsVisible = path is not null;
        show.Tag = path;
        show.IsVisible = path is not null && File.Exists(path);
        box.IsVisible = true;
    }

    private void HideResultBanner(string banner)
        => FindPageControl<Border>(banner)!.IsVisible = false;

    /// <summary>"Show in folder": open the OS file browser with the result file selected.</summary>
    internal void OnShowResultInFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || !File.Exists(path))
            return;
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", $"-R \"{path}\"");
            else
                System.Diagnostics.Process.Start("xdg-open", $"\"{Path.GetDirectoryName(path)}\"");
        }
        catch (Exception ex)
        {
            Vm.ReportError("Could not open the folder", ex);
        }
    }
}
