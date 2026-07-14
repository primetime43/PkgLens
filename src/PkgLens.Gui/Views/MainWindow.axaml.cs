using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow : Window
{
    private Border? _dropOverlay;

    public MainWindow()
    {
        InitializeComponent();
        // handledEventsToo: true so the drop still reaches the window even when a child control
        // (e.g. the file list) marks the drag event handled.
        AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: true);
        _dropOverlay = this.FindControl<Border>("DropOverlay");
        UpdateThemeChecks(Application.Current?.RequestedThemeVariant ?? ThemeVariant.Default);
        PopulatePackContentTypes();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnThemeSystem(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Default);
    private void OnThemeLight(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Light);
    private void OnThemeDark(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Dark);

    private void ApplyTheme(ThemeVariant variant)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = variant;
        ThemeSettings.Save(variant);
        UpdateThemeChecks(variant);
    }

    private void UpdateThemeChecks(ThemeVariant variant)
    {
        if (this.FindControl<MenuItem>("ThemeSystemItem") is { } s) s.IsChecked = variant == ThemeVariant.Default;
        if (this.FindControl<MenuItem>("ThemeLightItem") is { } l) l.IsChecked = variant == ThemeVariant.Light;
        if (this.FindControl<MenuItem>("ThemeDarkItem") is { } d) d.IsChecked = variant == ThemeVariant.Dark;
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

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

    private async void OnSetKeyClick(object? sender, RoutedEventArgs e)
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
            Vm.Status = $"Could not save key: {ex.Message}";
        }
    }

    private async void OnKeysClick(object? sender, RoutedEventArgs e)
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

    private async void OnExtractClick(object? sender, RoutedEventArgs e)
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

        try
        {
            await Task.Run(() => package.ExtractSelectedTo(dest));
            Vm.Status = $"Extracted {node.Name} → {dest}";
        }
        catch (Exception ex)
        {
            Vm.Status = $"Extract failed: {ex.Message}";
        }
    }

    // Right-click selects the row under the cursor so the context menu acts on it.
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
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

        try
        {
            Vm.Status = "Extracting all files…";
            int n = await Task.Run(() => package.ExtractAllTo(dest));
            Vm.Status = $"Extracted {n} file(s) to {dest}";
        }
        catch (Exception ex)
        {
            Vm.Status = $"Extract all failed: {ex.Message}";
        }
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm.Package?.SelectedItem is not { } node)
            return;

        if (node.IsDirectory)
            Vm.Package.OpenFolder(node);
        else if (node.Name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) && Vm.Package.CanEditSfo)
            EditSfo();   // SFO opens in the editor, not the hex viewer
        else
            ViewSelected();
    }

    private void OnViewClick(object? sender, RoutedEventArgs e) => ViewSelected();

    private async void ViewSelected()
    {
        if (Vm.Package is not { SelectedItem: { IsDirectory: false, Entry: not null } node } package)
            return;

        if (node.Size > PackageViewModel.MaxPreviewBytes)
        {
            Vm.Status = $"{node.Name} is too large to preview ({EntryNode.FormatSize(node.Size)}). Use Extract instead.";
            return;
        }

        try
        {
            byte[] data = await Task.Run(package.ReadSelectedBytes);
            string title = node.Name;

            // If it's an EDAT/SDAT, decrypt it so the viewer shows the real contents.
            if (PkgLens.Core.Npd.EdatFile.IsEdat(data))
                (data, title) = await DecryptEdatForView(data, node.Name);

            await new FileViewerDialog(title, data).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Vm.Status = $"Could not read {node.Name}: {ex.Message}";
        }
    }

    /// <summary>Decrypts an EDAT/SDAT for viewing (SDAT/free automatic; licensed resolves a RAP).</summary>
    private async Task<(byte[] data, string title)> DecryptEdatForView(byte[] data, string name)
    {
        var npd = PkgLens.Core.Npd.EdatFile.ParseHeader(new MemoryStream(data));

        byte[]? klic = null;
        if (npd.NeedsKlicensee)
        {
            byte[]? rap = PkgLens.Core.Npd.RapStore.Find(npd.ContentId) ?? await PromptForRap(npd.ContentId);
            if (rap is null)
            {
                Vm.Status = $"{npd.ContentId}: licensed EDAT — no RAP provided, showing the raw encrypted file.";
                return (data, name);
            }
            klic = PkgLens.Core.Npd.NpdKeys.RapToKlicensee(rap);
        }

        try
        {
            byte[] plain = PkgLens.Core.Npd.EdatFile.DecryptToArray(new MemoryStream(data), klic);
            Vm.Status = $"Decrypted EDAT: {npd.ContentId} (DRM {npd.LicenseText})";
            return (plain, $"{name}  ·  decrypted EDAT");
        }
        catch (Exception ex)
        {
            Vm.Status = $"EDAT decrypt failed: {ex.Message}";
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
        try { PkgLens.Core.Npd.RapStore.Install(contentId, rap); } catch { /* best-effort caching */ }
        return rap;
    }

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
        var box = this.FindControl<ComboBox>("PackContentTypeBox")!;
        box.ItemsSource = PackTypeChoices.Select(t => $"{t} (0x{(uint)t:X})").ToList();
        box.SelectedIndex = 0; // GameExec
    }

    /// <summary>Menu/toolbar "Pack folder…" simply switches to the Pack page.</summary>
    private void OnGoPackClick(object? sender, RoutedEventArgs e) => Vm.ActiveTool = ToolPage.Pack;

    private string? _packRapPath;

    private async void OnPackPickRap(object? sender, RoutedEventArgs e)
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
        this.FindControl<TextBlock>("PackRapText")!.Text = Path.GetFileName(path);
    }

    private async void OnPackFolderInfo(object? sender, RoutedEventArgs e)
    {
        if (_packFolder is null)
        {
            Vm.Status = "Choose a source folder first.";
            return;
        }

        var box = this.FindControl<Border>("FolderInfoBox")!;
        var text = this.FindControl<TextBlock>("FolderInfoText")!;
        try
        {
            var report = await Task.Run(() => PkgLens.Core.GameFolderInfo.Describe(_packFolder));
            text.Text = DescribeFolder(report);
            box.IsVisible = true;
            Vm.Status = $"Folder: {report.FileCount} file(s), {report.TotalBytes:n0} bytes.";
        }
        catch (Exception ex)
        {
            box.IsVisible = false;
            Vm.Status = $"Couldn't read folder: {ex.Message}";
        }
    }

    private static string DescribeFolder(PkgLens.Core.GameFolderReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Content ID   : {r.ContentId ?? "(unknown)"}");
        sb.AppendLine($"Title        : {r.Title ?? "(none)"}");
        sb.AppendLine($"Title ID     : {r.TitleId ?? "(none)"}");
        if (r.AppVersion is not null) sb.AppendLine($"App version  : {r.AppVersion}");
        if (r.Category is not null) sb.AppendLine($"Category     : {r.Category}");
        sb.AppendLine($"Content type : {r.ContentTypeGuess ?? "(unknown)"}");
        sb.AppendLine($"Contents     : {r.FileCount} file(s), {r.DirectoryCount} folder(s), {r.TotalBytes:n0} bytes");
        foreach (var eb in r.Eboots)
        {
            string state = eb.State switch
            {
                PkgLens.Core.EbootState.EncryptedSigned => "encrypted / signed",
                PkgLens.Core.EbootState.FakeSigned => "fake-signed (fSELF, CFW-ready)",
                PkgLens.Core.EbootState.PlainElf => "plain ELF",
                _ => "unknown",
            };
            sb.AppendLine($"EBOOT        : {eb.RelativePath}  [{state}]");
            if (eb.License is not null) sb.AppendLine($"  license    : {eb.License}" + (eb.Npdrm ? "  (NPDRM)" : ""));
        }
        if (r.Edats.Count > 0)
        {
            sb.AppendLine($"Data files   : {r.Edats.Count} EDAT/SDAT");
            foreach (var d in r.Edats)
                sb.AppendLine($"  {d.RelativePath}  [{(d.IsSdat ? "SDAT" : "EDAT")}, {d.License}{(d.NeedsRap ? ", needs RAP" : "")}]");
        }
        return sb.ToString().TrimEnd();
    }

    private void OnPackDrmChanged(object? sender, Avalonia.Controls.NumericUpDownValueChangedEventArgs e)
    {
        var label = this.FindControl<TextBlock>("PackDrmName");
        if (label is not null)
            label.Text = PkgLens.Core.Models.DrmType.Name((uint)(e.NewValue ?? 3));
    }

    private async void OnPackBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the content folder to pack",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder)
            return;

        _packFolder = folder;
        this.FindControl<TextBlock>("PackFolderText")!.Text = folder;

        // Fast-Pack inference to pre-fill the fields (blank fallback if it can't infer).
        try
        {
            var plan = await Task.Run(() => PkgLens.Core.FolderPackage.Plan(folder));
            this.FindControl<TextBox>("PackContentIdBox")!.Text = plan.ContentId;
            this.FindControl<TextBox>("PackInstallDirBox")!.Text = plan.InstallDirectory;
            this.FindControl<NumericUpDown>("PackDrmBox")!.Value = plan.DrmType;
            SelectPackContentType(plan.ContentType);
            ShowPackNotes(plan.Notes.Count > 0 ? "Inferred: " + string.Join("; ", plan.Notes) : null);
            Vm.Status = $"Ready to pack {plan.FileCount} file(s) from {Path.GetFileName(folder)}.";
        }
        catch (Exception ex)
        {
            ShowPackNotes($"Couldn't infer from PARAM.SFO — enter the content id manually. ({ex.Message})");
        }
    }

    private async void OnPackBuild(object? sender, RoutedEventArgs e)
    {
        if (_packFolder is null)
        {
            Vm.Status = "Choose a source folder first.";
            return;
        }

        string contentId = (this.FindControl<TextBox>("PackContentIdBox")!.Text ?? string.Empty).Trim();
        if (contentId.Length == 0)
        {
            Vm.Status = "A content id is required (e.g. UP0001-NPUB30910_00-EXAMPLE000000001).";
            return;
        }

        string installDir = (this.FindControl<TextBox>("PackInstallDirBox")!.Text ?? string.Empty).Trim();
        bool retail = this.FindControl<RadioButton>("PackRetailRadio")!.IsChecked == true;
        bool resign = this.FindControl<CheckBox>("PackResignCheck")!.IsChecked == true;

        byte[]? ebootKlic = null;
        if (resign && _packRapPath is not null)
        {
            try
            {
                byte[] rapBytes = File.ReadAllBytes(_packRapPath);
                if (rapBytes.Length != 16) { Vm.Status = "The chosen RAP is not 16 bytes."; return; }
                ebootKlic = PkgLens.Core.Npd.NpdKeys.RapToKlicensee(rapBytes);
            }
            catch (Exception ex) { Vm.Status = $"Couldn't read the RAP: {ex.Message}"; return; }
        }

        var options = new PkgLens.Core.PackOptions
        {
            ContentId = contentId,
            InstallDirectory = installDir.Length == 0 ? null : installDir,
            ContentType = SelectedPackContentType(),
            DrmType = (uint)(this.FindControl<NumericUpDown>("PackDrmBox")!.Value ?? 3),
            Finalization = retail ? PkgFinalization.Retail : PkgFinalization.Debug,
            ResignEboot = resign,
            EbootKlicensee = ebootKlic,
        };

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the new .pkg as…",
            SuggestedFileName = SanitizeFileName(contentId) + ".pkg",
            DefaultExtension = "pkg",
            FileTypeChoices = new[] { new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } } },
        });
        if (file?.TryGetLocalPath() is not { } dest)
            return;

        string folder = _packFolder;
        try
        {
            Vm.Status = "Packing…";
            var plan = await Task.Run(() =>
            {
                var p = PkgLens.Core.FolderPackage.Plan(folder, options);
                IKeyProvider keys = new FileKeyProvider(Vm.KeysDirectory);
                using var dst = File.Create(dest);
                p.Builder.Build(dst, keys);
                return p;
            });

            Vm.Status = retail
                ? $"Packed {plan.FileCount} file(s) → {Path.GetFileName(dest)} — retail-encrypted (installs on CFW)."
                : $"Packed {plan.FileCount} file(s) → {Path.GetFileName(dest)} — non-finalized (debug; RPCS3 / dev).";
            ShowPackNotes($"Wrote {new FileInfo(dest).Length:n0} bytes to {dest}");
        }
        catch (Exception ex)
        {
            Vm.Status = $"Pack failed: {ex.Message}";
        }
    }

    private void SelectPackContentType(uint value)
    {
        int idx = Array.FindIndex(PackTypeChoices, t => (uint)t == value);
        if (idx >= 0) this.FindControl<ComboBox>("PackContentTypeBox")!.SelectedIndex = idx;
    }

    private uint SelectedPackContentType()
    {
        int idx = this.FindControl<ComboBox>("PackContentTypeBox")!.SelectedIndex;
        return idx >= 0 ? (uint)PackTypeChoices[idx] : (uint)PkgContentType.GameExec;
    }

    private void ShowPackNotes(string? text)
    {
        var notes = this.FindControl<TextBlock>("PackNotes")!;
        notes.Text = text ?? string.Empty;
        notes.IsVisible = !string.IsNullOrEmpty(text);
    }

    // ==================== Decrypt page ====================

    private string? _decryptFile;
    private string? _decryptRap;

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
        ShowDecryptNotes(null);
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

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(_decryptFile);
            var npd = PkgLens.Core.Npd.EdatFile.ParseHeader(new MemoryStream(bytes));

            byte[]? klic = null;
            if (npd.NeedsKlicensee)
            {
                byte[]? rap = _decryptRap is not null
                    ? await File.ReadAllBytesAsync(_decryptRap)
                    : PkgLens.Core.Npd.RapStore.Find(npd.ContentId);
                if (rap is null)
                {
                    Vm.Status = $"{npd.ContentId} is a licensed EDAT — choose its RAP (Browse next to “RAP”).";
                    return;
                }
                klic = PkgLens.Core.Npd.NpdKeys.RapToKlicensee(rap);
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
                using var input = File.OpenRead(src);
                using var output = File.Create(dest);
                PkgLens.Core.Npd.EdatFile.Decrypt(input, output, k);
            });
            Vm.Status = $"Decrypted {npd.ContentId} → {Path.GetFileName(dest)}";
            ShowDecryptNotes($"Wrote {new FileInfo(dest).Length:n0} bytes to {dest}");
        }
        catch (Exception ex)
        {
            Vm.Status = $"Decrypt failed: {ex.Message}";
        }
    }

    private void ShowDecryptNotes(string? text)
    {
        var notes = this.FindControl<TextBlock>("DecryptNotes")!;
        notes.Text = text ?? string.Empty;
        notes.IsVisible = !string.IsNullOrEmpty(text);
    }

    // ==================== Resign page (inspector for now) ====================

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

        try
        {
            var info = await Task.Run(() =>
            {
                using var s = File.OpenRead(path);
                return PkgLens.Core.Self.SelfReader.ParseInfo(s);
            });
            text.Text = DescribeSelf(info);
            box.IsVisible = true;
            Vm.Status = $"Read SELF header: {info.ProgramTypeText}" + (info.IsNpdrm ? " (NPDRM)" : "");
        }
        catch (Exception ex)
        {
            box.IsVisible = false;
            Vm.Status = $"Not a readable SELF: {ex.Message}";
        }
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

        var opts = new PkgLens.Core.Self.SelfBuilder.FakeSelfOptions { Npdrm = npdrm };
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

        try
        {
            long size = await Task.Run(() =>
            {
                byte[] elf = File.ReadAllBytes(input);
                byte[] fself = PkgLens.Core.Self.SelfBuilder.MakeFakeSelf(elf, opts);
                File.WriteAllBytes(dest, fself);
                return (long)fself.Length;
            });
            Vm.Status = $"Fake-signed → {Path.GetFileName(dest)} ({size:n0} bytes).";
            ShowFselfNotes($"Wrote a {(npdrm ? "NPDRM" : "NON-DRM")} fSELF (key rev 0x8000). Runs on CFW; not on stock retail.");
        }
        catch (Exception ex)
        {
            Vm.Status = $"Fake-sign failed: {ex.Message}";
            ShowFselfNotes(ex.Message);
        }
    }

    private void ShowFselfNotes(string? text)
    {
        var notes = this.FindControl<TextBlock>("FselfNotes")!;
        notes.Text = text ?? string.Empty;
        notes.IsVisible = !string.IsNullOrEmpty(text);
    }

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

        try
        {
            string summary = await Task.Run(() =>
            {
                byte[] raw = File.ReadAllBytes(input);
                uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw);

                byte[] elf; bool npdrm = false;
                if (magic == 0x53434500) // SCE — decrypt first
                {
                    byte[]? klic = null;
                    if (rap is not null)
                    {
                        byte[] rb = File.ReadAllBytes(rap);
                        if (rb.Length != 16) throw new PkgLens.Core.PkgFormatException("A RAP must be 16 bytes.");
                        klic = PkgLens.Core.Npd.NpdKeys.RapToKlicensee(rb);
                    }
                    var dec = PkgLens.Core.Self.SelfDecryptor.Decrypt(raw, klic);
                    elf = dec.Elf; npdrm = dec.WasNpdrm;
                }
                else if (magic == 0x7F454C46) // ELF
                {
                    elf = raw;
                }
                else throw new PkgLens.Core.PkgFormatException("Input is neither an ELF nor a SELF/EBOOT.BIN.");

                var prev = PkgLens.Core.Self.EbootPatcher.SetFirmwareVersion(elf, major, minor);
                string fwNote = prev is null
                    ? "no sys_process_param found — firmware left unchanged"
                    : $"firmware {prev.Display} → {major}.{minor:D2}";

                byte[] fself = PkgLens.Core.Self.SelfBuilder.MakeFakeSelf(elf, npdrm);
                File.WriteAllBytes(dest, fself);
                return $"Magic-patched → {Path.GetFileName(dest)} ({fself.Length:n0} bytes); {fwNote}.";
            });
            Vm.Status = summary;
            ShowMagicNotes(summary + "  Runs on CFW; not on stock retail.");
        }
        catch (Exception ex)
        {
            Vm.Status = $"Magic patch failed: {ex.Message}";
            ShowMagicNotes(ex.Message);
        }
    }

    /// <summary>Reads the optional Custom Sign fields into <paramref name="opts"/>. Blank fields are left at defaults.</summary>
    private bool TryReadFselfCustomFields(PkgLens.Core.Self.SelfBuilder.FakeSelfOptions opts, out string? error)
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

    private string? _unselfRapPath;

    private async void OnPickRap(object? sender, RoutedEventArgs e)
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
        this.FindControl<TextBlock>("UnselfRapText")!.Text = Path.GetFileName(path);
    }

    private async void OnUnself(object? sender, RoutedEventArgs e)
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

        bool chain = this.FindControl<CheckBox>("UnselfThenResignCheck")!.IsChecked == true;
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

        try
        {
            var summary = await Task.Run(() =>
            {
                byte[] self = File.ReadAllBytes(input);
                byte[]? klic = null;
                if (rap is not null)
                {
                    byte[] rapBytes = File.ReadAllBytes(rap);
                    if (rapBytes.Length != 16) throw new PkgLens.Core.PkgFormatException("A RAP file must be exactly 16 bytes.");
                    klic = PkgLens.Core.Npd.NpdKeys.RapToKlicensee(rapBytes);
                }

                var result = PkgLens.Core.Self.SelfDecryptor.Decrypt(self, klic);
                string lic = result.WasNpdrm ? (result.License?.ToString() ?? "NPDRM") : "non-NPDRM";

                if (chain)
                {
                    byte[] fself = PkgLens.Core.Self.SelfBuilder.MakeFakeSelf(result.Elf, npdrm: result.WasNpdrm);
                    File.WriteAllBytes(dest, fself);
                    // Drop the intermediate ELF beside the fSELF for reference.
                    string elfBeside = Path.Combine(Path.GetDirectoryName(dest) ?? "", baseName + ".ELF");
                    File.WriteAllBytes(elfBeside, result.Elf);
                    return $"Decrypted ({lic}) → fake-signed fSELF {Path.GetFileName(dest)} ({fself.Length:n0} bytes); ELF beside it.";
                }

                File.WriteAllBytes(dest, result.Elf);
                return $"Decrypted {self.Length:n0}-byte SELF ({lic}) → {Path.GetFileName(dest)} ({result.Elf.Length:n0} bytes).";
            });
            Vm.Status = summary;
            ShowFselfNotes(summary);
        }
        catch (Exception ex)
        {
            Vm.Status = $"Decrypt failed: {ex.Message}";
            ShowFselfNotes(ex.Message);
        }
    }

    private static string DescribeSelf(PkgLens.Core.Self.SelfInfo s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Program    : {s.ProgramTypeText}" + (s.IsNpdrm ? "  (NPDRM)" : ""));
        sb.AppendLine($"Key rev    : 0x{s.KeyRevision:X4}" + (s.IsLikelyFakeSigned ? "  (fake-signed / fSELF)" : ""));
        sb.AppendLine($"Auth ID    : 0x{s.AuthId:X16}");
        sb.AppendLine($"Vendor ID  : 0x{s.VendorId:X8}");
        sb.AppendLine($"ELF size   : {s.DataLength:n0} bytes (decrypted)");
        if (s.Elf is { } elf)
            sb.AppendLine($"ELF        : {(elf.Is64Bit ? "64-bit" : "32-bit")} {(elf.IsBigEndian ? "big-endian" : "little-endian")}, {elf.TypeText}");
        if (s.ControlBlocks.Count > 0)
            sb.AppendLine($"Control    : {string.Join(", ", s.ControlBlocks.Select(b => b.TypeText))}");
        if (s.ControlFlags is { } cf)
            sb.AppendLine($"Ctrl flags : {Convert.ToHexString(cf)}");
        if (s.FirmwareVersionText is { } fw)
            sb.AppendLine($"FW version : {fw}");
        if (s.Segments.Count > 0)
        {
            sb.AppendLine($"Segments   : {s.Segments.Count}");
            foreach (var seg in s.Segments)
                sb.AppendLine($"  [{seg.Index}] offset 0x{seg.Offset:X}  size {seg.Size:n0}  {seg.CompressedText}, {seg.EncryptedText}");
        }
        if (s.Npdrm is { } npd)
        {
            sb.AppendLine($"Content ID : {npd.ContentId}");
            sb.AppendLine($"License    : {npd.LicenseText}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string SanitizeFileName(string? name)
    {
        name ??= string.Empty;
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrEmpty(name) ? "package" : name;
    }

    private void OnInfoClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is { } package)
            new PackageInfoDialog { DataContext = package }.ShowDialog(this);
    }

    private void OnEditSfoClick(object? sender, RoutedEventArgs e) => EditSfo();

    private async void EditSfo()
    {
        if (Vm.Package is not { Sfo: { } sfo } package)
            return;

        var edited = await new SfoEditorDialog(sfo.Entries).ShowDialog<List<PkgLens.Core.Sfo.SfoEntry>?>(this);
        if (edited is null)
            return;

        try
        {
            package.ApplySfoEdits(edited);
            Vm.Status = $"PARAM.SFO edited — {package.PendingChangeCount} pending change(s). Use File → Save As to write a new .pkg.";
        }
        catch (Exception ex)
        {
            Vm.Status = $"SFO edit failed: {ex.Message}";
        }
    }

    private async void OnVerifyClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { } package)
            return;
        try
        {
            var report = await Task.Run(package.Verify);
            await new VerifyDialog(report).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Vm.Status = $"Verify failed: {ex.Message}";
        }
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e) =>
        new AboutDialog().ShowDialog(this);

    private async void OnReplaceClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { SelectedItem: { IsDirectory: false, Entry: not null } node } package)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replace {node.Name} with…",
            AllowMultiple = false,
            FileTypeFilter = ReplaceFilters(node.Name),
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        try
        {
            byte[] content = await File.ReadAllBytesAsync(path);
            package.ReplaceSelected(content);
            Vm.Status = $"Replaced {node.Name} ({EntryNode.FormatSize(node.Size)} → " +
                        $"{EntryNode.FormatSize((ulong)content.Length)}). {package.PendingChangeCount} pending change(s) — " +
                        "use File → Save As to write a new .pkg.";
        }
        catch (Exception ex)
        {
            Vm.Status = $"Replace failed: {ex.Message}";
        }
    }

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>
    /// Builds Open-picker filters so Replace defaults to the selected entry's type — e.g. an image
    /// entry offers image formats first — while still allowing "All files" as a deliberate override.
    /// </summary>
    private static IReadOnlyList<FilePickerFileType> ReplaceFilters(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        var filters = new List<FilePickerFileType>();

        if (ImageExtensions.Contains(ext))
        {
            filters.Add(new FilePickerFileType($"{ext.TrimStart('.').ToUpperInvariant()} image")
            { Patterns = new[] { "*" + ext } });
            filters.Add(new FilePickerFileType("Images")
            { Patterns = ImageExtensions.Select(e => "*" + e).ToArray() });
        }
        else if (!string.IsNullOrEmpty(ext))
        {
            filters.Add(new FilePickerFileType($"{ext.TrimStart('.').ToUpperInvariant()} files")
            { Patterns = new[] { "*" + ext } });
        }

        filters.Add(FilePickerFileTypes.All);
        return filters;
    }

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { } package)
            return;

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
            return;

        try
        {
            Vm.Status = "Repacking…";
            await Task.Run(() => package.SaveAs(dest));
            Vm.Status = package.IsRetail
                ? $"Saved {System.IO.Path.GetFileName(dest)} — UNSIGNED (retail: invalid CMAC/signature; won't install on a real console)."
                : $"Saved {System.IO.Path.GetFileName(dest)}.";
        }
        catch (Exception ex)
        {
            Vm.Status = $"Save failed: {ex.Message}";
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Vm.CloseFile();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool hasFiles = e.Data.Contains(DataFormats.Files);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDropOverlay(hasFiles);
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => ShowDropOverlay(false);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ShowDropOverlay(false);

        var files = e.Data.GetFiles();
        if (files is null)
            return;

        foreach (var item in files)
        {
            if (item is IStorageFile file &&
                file.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) &&
                file.TryGetLocalPath() is { } path)
            {
                await Vm.LoadAsync(path);
                return;
            }
        }

        Vm.Status = "Drop a .pkg file to open it.";
    }

    private void ShowDropOverlay(bool show)
    {
        if (_dropOverlay is not null)
            _dropOverlay.IsVisible = show;
    }
}
