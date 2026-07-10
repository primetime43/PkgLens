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

        var options = new PkgLens.Core.PackOptions
        {
            ContentId = contentId,
            InstallDirectory = installDir.Length == 0 ? null : installDir,
            ContentType = SelectedPackContentType(),
            DrmType = (uint)(this.FindControl<NumericUpDown>("PackDrmBox")!.Value ?? 3),
            Finalization = retail ? PkgFinalization.Retail : PkgFinalization.Debug,
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
                byte[] fself = PkgLens.Core.Self.SelfBuilder.MakeFakeSelf(elf, npdrm);
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
