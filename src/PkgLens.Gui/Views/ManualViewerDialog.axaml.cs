using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

/// <summary>A pager over a decrypted DOCUMENT.DAT manual — one PNG page at a time, with "Save all".</summary>
public partial class ManualViewerDialog : Window
{
    private readonly IReadOnlyList<byte[]> _pages = Array.Empty<byte[]>();
    private int _index;

    private Image _preview = null!;
    private TextBlock _label = null!;

    public ManualViewerDialog() => InitializeComponent();

    public ManualViewerDialog(string title, IReadOnlyList<byte[]> pages)
    {
        InitializeComponent();
        _pages = pages;
        Title = $"Manual — {title} ({pages.Count} page{(pages.Count == 1 ? "" : "s")})";
        ShowPage(0);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _preview = this.FindControl<Image>("Preview")!;
        _label = this.FindControl<TextBlock>("PageLabel")!;
    }

    private void ShowPage(int index)
    {
        if (_pages.Count == 0) { _label.Text = "No pages"; return; }
        _index = Math.Clamp(index, 0, _pages.Count - 1);
        try
        {
            using var ms = new MemoryStream(_pages[_index]);
            _preview.Source = new Bitmap(ms);
        }
        catch { /* leave the previous image if a page fails to decode */ }
        _label.Text = $"Page {_index + 1} / {_pages.Count}";
        this.FindControl<Button>("PrevBtn")!.IsEnabled = _index > 0;
        this.FindControl<Button>("NextBtn")!.IsEnabled = _index < _pages.Count - 1;
    }

    private void OnPrev(object? sender, RoutedEventArgs e) => ShowPage(_index - 1);
    private void OnNext(object? sender, RoutedEventArgs e) => ShowPage(_index + 1);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnSaveAll(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save manual pages to…",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } dir)
            return;

        try
        {
            for (int i = 0; i < _pages.Count; i++)
                await AtomicOutput.WriteAllBytesAsync(Path.Combine(dir, $"page_{i + 1:D3}.png"), _pages[i]);
            _label.Text = $"Saved {_pages.Count} page(s) to {dir}";
        }
        catch (Exception ex)
        {
            _label.Text = $"Save failed: {ex.Message}";
            await new ErrorDialog(new GuiErrorReport(
                "Save failed", ex.Message, ex.ToString())).ShowDialog(this);
        }
    }
}
