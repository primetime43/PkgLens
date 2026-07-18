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
    // ==================== Resign page ====================

    // Detail panels keyed by the card Tag that reveals them.
    private static readonly (string Key, string Panel)[] ResignOps =
    {
        ("inspect",  "ResignOpInspect"),
        ("fakesign", "ResignOpFakeSign"),
        ("decrypt",  "ResignOpDecrypt"),
        ("magic",    "ResignOpMagic"),
        ("byte",     "ResignOpByte"),
    };

    /// <summary>A card was clicked: hide the menu and show just that operation's controls.</summary>
    private void OnResignCardClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string key })
            ShowResignOp(key);
    }

    /// <summary>Back link: return to the card menu.</summary>
    private void OnResignBack(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Control>("ResignMenu")!.IsVisible = true;
        this.FindControl<Control>("ResignDetail")!.IsVisible = false;
    }

    private void ShowResignOp(string key)
    {
        this.FindControl<Control>("ResignMenu")!.IsVisible = false;
        this.FindControl<Control>("ResignDetail")!.IsVisible = true;
        foreach (var (opKey, panel) in ResignOps)
            this.FindControl<Control>(panel)!.IsVisible = opKey == key;
    }

    // ==================== Home page (main menu) ====================

    private void OnHomeOpenPkg(object? sender, RoutedEventArgs e) => OnOpenClick(sender, e);
    private void OnHomeScan(object? sender, RoutedEventArgs e) => OnScanFolderClick(sender, e);
    private void OnHomeFolderInfo(object? sender, RoutedEventArgs e) => OnFolderInfoClick(sender, e);
    private void OnHomePack(object? sender, RoutedEventArgs e) => Vm.ActiveTool = ToolPage.Pack;
    private void OnHomeDecrypt(object? sender, RoutedEventArgs e) => Vm.ActiveTool = ToolPage.Decrypt;
    private void OnHomeKeys(object? sender, RoutedEventArgs e) => Vm.ActiveTool = ToolPage.Keys;

    /// <summary>A Home "SELF / EBOOT" card: open the Resign page focused on that operation.</summary>
    private void OnHomeResign(object? sender, RoutedEventArgs e)
    {
        Vm.ActiveTool = ToolPage.Resign;
        if (sender is Control { Tag: string key })
            ShowResignOp(key);
    }

    private async void OnSelfBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an EBOOT.BIN / SELF / SPRX",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SELF / EBOOT") { Patterns = new[] { "EBOOT.BIN", "*.self", "*.sprx", "*.bin" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        this.FindControl<TextBlock>("SelfFileText")!.Text = Path.GetFileName(path);
        var box = this.FindControl<Border>("SelfInfoBox")!;
        var text = this.FindControl<TextBlock>("SelfInfoText")!;

        await RunOperationAsync("Inspecting SELF…", "SELF inspection failed", async (token, _) =>
        {
            var info = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var s = File.OpenRead(path);
                return PkgLens.Core.Ps3.Self.SelfReader.ParseInfo(s);
            }, token);
            text.Text = PackagePresentationService.DescribeSelf(info);
            box.IsVisible = true;
            Vm.Status = $"Read SELF header: {info.ProgramTypeText}" + (info.IsNpdrm ? " (NPDRM)" : "");
        });
    }

    private async void OnMakeFself(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a decrypted ELF to fake-sign",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ELF") { Patterns = new[] { "*.elf", "*.ELF", "EBOOT.ELF" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } input)
            return;

        bool npdrm = this.FindControl<CheckBox>("FselfNpdrmCheck")!.IsChecked == true;

        var opts = new PkgLens.Core.Ps3.Self.SelfBuilder.FakeSelfOptions { Npdrm = npdrm };
        if (!TryReadFselfCustomFields(opts, out string? fieldError))
        {
            Vm.Status = fieldError!;
            ShowFselfNotes(fieldError);
            return;
        }

        var save = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the fake-signed SELF as…",
            SuggestedFileName = npdrm ? "EBOOT.BIN" : Path.GetFileNameWithoutExtension(input) + ".self",
            DefaultExtension = npdrm ? "BIN" : "self",
        });
        if (save?.TryGetLocalPath() is not { } dest)
            return;

        await RunOperationAsync("Fake-signing SELF…", "Fake-sign failed", async (token, _) =>
        {
            long size = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                byte[] elf = File.ReadAllBytes(input);
                byte[] fself = PkgLens.Core.Ps3.Self.SelfBuilder.MakeFakeSelf(elf, opts);
                AtomicOutput.EnsureDifferentPath(input, dest);
                AtomicOutput.WriteAllBytes(dest, fself);
                PostOperationVerifier.VerifyFakeSelf(dest, elf);
                return (long)fself.Length;
            }, token);
            Vm.Status = $"Fake-signed and verified → {Path.GetFileName(dest)} ({size:n0} bytes).";
            ShowFselfNotes($"Wrote a {(npdrm ? "NPDRM" : "NON-DRM")} fSELF (key rev 0x8000). Runs on CFW; not on stock retail.");
        });
    }

    private void ShowFselfNotes(string? text)
    {
        var notes = this.FindControl<TextBlock>("FselfNotes")!;
        notes.Text = text ?? string.Empty;
        notes.IsVisible = !string.IsNullOrEmpty(text);
    }

    private bool TryReadFselfCustomFields(PkgLens.Core.Ps3.Self.SelfBuilder.FakeSelfOptions opts, out string? error)
    {
        error = null;
        if (!TryHexU64(this.FindControl<TextBox>("FselfAuthIdBox")!.Text, "Auth ID", out var authId, out error)) return false;
        if (authId is not null) opts.AuthId = authId;

        if (!TryHexU64(this.FindControl<TextBox>("FselfVendorIdBox")!.Text, "Vendor ID", out var vendor, out error)) return false;
        if (vendor is not null)
        {
            if (vendor > uint.MaxValue) { error = "Vendor ID must fit in 32 bits."; return false; }
            opts.VendorId = (uint)vendor;
        }

        if (!TryHexU64(this.FindControl<TextBox>("FselfAppVerBox")!.Text, "App version", out var ver, out error)) return false;
        if (ver is not null) opts.AppVersion = ver;

        string fw = (this.FindControl<TextBox>("FselfFwVerBox")!.Text ?? string.Empty).Trim();
        if (fw.Length > 0)
        {
            string[] parts = fw.Split('.');
            if (parts.Length != 2 || !uint.TryParse(parts[0], out uint major) ||
                !uint.TryParse(parts[1], out uint minor) || minor >= 100)
            {
                error = "FW version must be M.NN (e.g. 4.46).";
                return false;
            }
            opts.FirmwareVersion = major * 10000UL + minor * 100UL;
        }

        string flagsText = (this.FindControl<TextBox>("FselfCtrlFlagsBox")!.Text ?? string.Empty).Trim();
        if (flagsText.Length > 0)
        {
            if (flagsText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) flagsText = flagsText[2..];
            flagsText = new string(flagsText.Where(c => !char.IsWhiteSpace(c)).ToArray());
            byte[]? flags = null;
            if (flagsText.Length == 0x40) { try { flags = Convert.FromHexString(flagsText); } catch (FormatException) { } }
            if (flags is null) { error = "Control flags must be 64 hex characters (0x20 bytes)."; return false; }
            opts.ControlFlags = flags;
        }

        string cid = (this.FindControl<TextBox>("FselfContentIdBox")!.Text ?? string.Empty).Trim();
        if (cid.Length > 0) opts.ContentId = cid;

        string npLic = (this.FindControl<TextBox>("FselfNpLicenseBox")!.Text ?? string.Empty).Trim();
        if (npLic.Length > 0)
        {
            if (!PkgLens.Core.Ps3.Self.SelfBuilder.TryParseNpLicenseType(npLic, out uint lic))
            {
                error = "NP license must be FREE, LOCAL or NETWORK.";
                return false;
            }
            opts.NpLicenseType = lic;
        }

        string npApp = (this.FindControl<TextBox>("FselfNpAppTypeBox")!.Text ?? string.Empty).Trim();
        if (npApp.Length > 0)
        {
            if (!PkgLens.Core.Ps3.Self.SelfBuilder.TryParseNpAppType(npApp, out uint at))
            {
                error = "NP app type must be SPRX, EXEC, USPRX or UEXEC.";
                return false;
            }
            opts.NpAppType = at;
        }
        return true;
    }

    private static bool TryHexU64(string? text, string label, out ulong? value, out string? error)
    {
        value = null; error = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return true; // blank → keep default
        string body = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (ulong.TryParse(body, System.Globalization.NumberStyles.HexNumber, null, out ulong v)) { value = v; return true; }
        error = $"{label} must be a hex number (e.g. 0x1010000001000003).";
        return false;
    }
}
