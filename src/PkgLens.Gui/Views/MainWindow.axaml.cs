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
            await new FileViewerDialog(node.Name, data).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Vm.Status = $"Could not read {node.Name}: {ex.Message}";
        }
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
