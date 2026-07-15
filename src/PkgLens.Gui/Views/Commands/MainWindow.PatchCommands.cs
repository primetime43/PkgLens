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
    private void ShowMagicNotes(string? text)
    {
        var notes = this.FindControl<TextBlock>("MagicNotes")!;
        notes.Text = text ?? string.Empty;
        notes.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async void OnMagicPatch(object? sender, RoutedEventArgs e)
    {
        string fw = (this.FindControl<TextBox>("MagicFwBox")!.Text ?? string.Empty).Trim();
        var parts = fw.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)
            || major is < 0 or > 15 || minor is < 0 or > 99)
        {
            Vm.Status = "Enter a target firmware like 4.00 first.";
            ShowMagicNotes("Enter a target firmware like 4.00 (major.minor) before patching.");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an EBOOT.BIN / SELF / ELF to magic-patch",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("EBOOT / SELF / ELF") { Patterns = new[] { "EBOOT.BIN", "*.self", "*.sprx", "*.elf", "*.bin" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } input)
            return;

        string? rap = _unselfRapPath;
        var save = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the patched, fake-signed EBOOT as…",
            SuggestedFileName = "EBOOT.BIN",
            DefaultExtension = "BIN",
        });
        if (save?.TryGetLocalPath() is not { } dest)
            return;

        await RunOperationAsync("Magic-patching executable…", "Magic patch failed", async (token, _) =>
        {
            string summary = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                byte[] raw = File.ReadAllBytes(input);
                uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw);

                byte[] elf; bool npdrm = false;
                if (magic == 0x53434500) // SCE — decrypt first
                {
                    RapLicenseResolution resolution = RapLicenseService.ResolveSelf(raw, rap, Vm.RapDirectory);
                    var dec = PkgLens.Core.Ps3.Self.SelfDecryptor.Decrypt(raw, resolution.Klicensee);
                    elf = dec.Elf; npdrm = dec.WasNpdrm;
                }
                else if (magic == 0x7F454C46) // ELF
                {
                    elf = raw;
                }
                else throw new PkgLens.Core.PkgFormatException("Input is neither an ELF nor a SELF/EBOOT.BIN.");

                var prev = PkgLens.Core.Ps3.Self.EbootPatcher.SetFirmwareVersion(elf, major, minor);
                string fwNote = prev is null
                    ? "no sys_process_param found — firmware left unchanged"
                    : $"firmware {prev.Display} → {major}.{minor:D2}";

                byte[] fself = PkgLens.Core.Ps3.Self.SelfBuilder.MakeFakeSelf(elf, npdrm);
                AtomicOutput.EnsureDifferentPath(input, dest);
                AtomicOutput.WriteAllBytes(dest, fself);
                return $"Magic-patched → {Path.GetFileName(dest)} ({fself.Length:n0} bytes); {fwNote}.";
            }, token);
            Vm.RefreshRapStatus();
            Vm.Status = summary;
            ShowMagicNotes(summary + "  Runs on CFW; not on stock retail.");
        });
    }

    private void ShowBytePatchNotes(string? text)
    {
        var notes = this.FindControl<TextBlock>("BytePatchNotes")!;
        notes.Text = text ?? string.Empty;
        notes.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async void OnBytePatch(object? sender, RoutedEventArgs e)
    {
        // Parse the find/replace and at-offset inputs up front so we fail before any file picker.
        string findText = (this.FindControl<TextBox>("PatchFindBox")!.Text ?? string.Empty).Trim();
        string replaceText = (this.FindControl<TextBox>("PatchReplaceBox")!.Text ?? string.Empty).Trim();
        string offsetText = (this.FindControl<TextBox>("PatchAtOffsetBox")!.Text ?? string.Empty).Trim();
        string atBytesText = (this.FindControl<TextBox>("PatchAtBytesBox")!.Text ?? string.Empty).Trim();

        byte[]? find = null, replace = null, atBytes = null;
        int atOffset = 0;

        bool hasFindReplace = findText.Length > 0 || replaceText.Length > 0;
        if (hasFindReplace)
        {
            if (!TryParseHex(findText, out find) || !TryParseHex(replaceText, out replace) || find.Length == 0)
            {
                Fail("Find and Replace must both be hex bytes.");
                return;
            }
            if (find.Length != replace.Length)
            {
                Fail("Find and Replace must be the same length.");
                return;
            }
        }

        bool hasAt = offsetText.Length > 0 || atBytesText.Length > 0;
        if (hasAt)
        {
            if (!TryParseOffset(offsetText, out atOffset) || !TryParseHex(atBytesText, out atBytes) || atBytes.Length == 0)
            {
                Fail("At-offset needs an offset (0x-hex or decimal) and hex bytes to write.");
                return;
            }
        }

        if (!hasFindReplace && !hasAt)
        {
            Fail("Nothing to patch — enter a find/replace pair and/or an at-offset edit.");
            return;
        }

        bool resign = this.FindControl<CheckBox>("BytePatchResignCheck")!.IsChecked == true;
        bool npdrmOverride = this.FindControl<CheckBox>("BytePatchNpdrmCheck")!.IsChecked == true;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an EBOOT.BIN / SELF / ELF to byte-patch",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("EBOOT / SELF / ELF") { Patterns = new[] { "EBOOT.BIN", "*.self", "*.sprx", "*.elf", "*.bin" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } input)
            return;

        string? rap = _unselfRapPath;
        var save = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the patched output as…",
            SuggestedFileName = Path.GetFileNameWithoutExtension(input) + ".patched",
        });
        if (save?.TryGetLocalPath() is not { } dest)
            return;

        await RunOperationAsync("Byte-patching executable…", "Byte patch failed", async (token, _) =>
        {
            string summary = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                byte[] raw = File.ReadAllBytes(input);
                if (raw.Length < 4) throw new PkgLens.Core.PkgFormatException("File is too small.");
                uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw);

                byte[] elf; bool wasSelf = false, npdrm = npdrmOverride;
                if (magic == 0x53434500) // SCE — decrypt first
                {
                    RapLicenseResolution resolution = RapLicenseService.ResolveSelf(raw, rap, Vm.RapDirectory);
                    var dec = PkgLens.Core.Ps3.Self.SelfDecryptor.Decrypt(raw, resolution.Klicensee);
                    elf = dec.Elf; wasSelf = true; npdrm = dec.WasNpdrm || npdrmOverride;
                }
                else if (magic == 0x7F454C46) // ELF
                {
                    elf = raw;
                }
                else throw new PkgLens.Core.PkgFormatException("Input is neither an ELF nor a SELF/EBOOT.BIN.");

                var steps = new List<string>();
                if (find is not null && replace is not null)
                {
                    int n = PkgLens.Core.Ps3.Self.EbootPatcher.PatchPattern(elf, find, replace);
                    steps.Add($"find/replace: {n} occurrence(s)");
                }
                if (atBytes is not null)
                {
                    PkgLens.Core.Ps3.Self.EbootPatcher.PatchAt(elf, atOffset, atBytes);
                    steps.Add($"at 0x{atOffset:X}: {atBytes.Length} byte(s)");
                }

                bool emitSelf = wasSelf || resign;
                byte[] output = emitSelf ? PkgLens.Core.Ps3.Self.SelfBuilder.MakeFakeSelf(elf, npdrm) : elf;
                AtomicOutput.EnsureDifferentPath(input, dest);
                AtomicOutput.WriteAllBytes(dest, output);

                string kind = emitSelf ? $"fake-signed {(npdrm ? "NPDRM" : "NON-DRM")} SELF" : "ELF";
                return $"Patched → {Path.GetFileName(dest)} ({output.Length:n0} bytes) [{kind}]; {string.Join("; ", steps)}.";
            }, token);
            Vm.RefreshRapStatus();
            Vm.Status = summary;
            ShowBytePatchNotes(summary + (summary.Contains("SELF") ? "  Runs on CFW; not on stock retail." : ""));
        });

        void Fail(string message) { Vm.Status = message; ShowBytePatchNotes(message); }
    }

    private static bool TryParseHex(string s, out byte[] bytes)
    {
        s = (s ?? string.Empty).Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        try { bytes = Convert.FromHexString(s); return true; }
        catch (FormatException) { bytes = Array.Empty<byte>(); return false; }
    }

    private static bool TryParseOffset(string s, out int offset)
    {
        s = (s ?? string.Empty).Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out offset);
        return int.TryParse(s, out offset);
    }

    /// <summary>Reads the optional Custom Sign fields into <paramref name="opts"/>. Blank fields are left at defaults.</summary>
}
