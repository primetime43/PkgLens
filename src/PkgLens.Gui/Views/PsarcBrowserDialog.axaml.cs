using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PkgLens.Core.Ps3.Psarc;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class PsarcBrowserDialog : Window
{
    private string? _archivePath;
    private PsarcArchiveInfo? _archive;
    private readonly Dictionary<PsarcEntry, PsarcReplacement> _replacements = new();
    private readonly Dictionary<PsarcEntry, string> _replacementPaths = new();
    private CancellationTokenSource? _cancellation;

    private TextBlock _archiveName = null!;
    private TextBlock _archivePathText = null!;
    private TextBlock _summary = null!;
    private TextBlock _status = null!;
    private TextBox _filter = null!;
    private DataGrid _grid = null!;
    private Button _view = null!;
    private Button _extract = null!;
    private Button _extractAll = null!;
    private Button _replace = null!;
    private Button _clearReplacement = null!;
    private Button _rebuild = null!;
    private Button _cancel = null!;
    private Button _close = null!;
    private ProgressBar _progress = null!;

    public PsarcBrowserDialog()
    {
        InitializeComponent();
        FindControls();
    }

    public PsarcBrowserDialog(string archivePath) : this()
    {
        OpenArchive(archivePath);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindControls()
    {
        _archiveName = this.FindControl<TextBlock>("ArchiveNameText")!;
        _archivePathText = this.FindControl<TextBlock>("ArchivePathText")!;
        _summary = this.FindControl<TextBlock>("SummaryText")!;
        _status = this.FindControl<TextBlock>("StatusText")!;
        _filter = this.FindControl<TextBox>("FilterText")!;
        _grid = this.FindControl<DataGrid>("EntriesGrid")!;
        _view = this.FindControl<Button>("ViewButton")!;
        _extract = this.FindControl<Button>("ExtractButton")!;
        _extractAll = this.FindControl<Button>("ExtractAllButton")!;
        _replace = this.FindControl<Button>("ReplaceButton")!;
        _clearReplacement = this.FindControl<Button>("ClearReplacementButton")!;
        _rebuild = this.FindControl<Button>("RebuildButton")!;
        _cancel = this.FindControl<Button>("CancelButton")!;
        _close = this.FindControl<Button>("CloseButton")!;
        _progress = this.FindControl<ProgressBar>("Progress")!;
    }

    private PsarcDisplayEntry? Selected => _grid.SelectedItem as PsarcDisplayEntry;

    private void OpenArchive(string path)
    {
        PsarcArchiveInfo archive = PsarcReader.Read(path);
        _archivePath = Path.GetFullPath(path);
        _archive = archive;
        _replacements.Clear();
        _replacementPaths.Clear();
        _archiveName.Text = Path.GetFileName(path);
        _archivePathText.Text = _archivePath;
        ToolTip.SetTip(_archivePathText, _archivePath);
        Title = $"PSARC browser — {Path.GetFileName(path)}";
        _summary.Text = $"v{archive.Header.MajorVersion}.{archive.Header.MinorVersion} · {archive.Header.Compression} · " +
                        $"{archive.Entries.Count:n0} files · {FormatSize(archive.TotalUncompressedBytes)}";
        _status.Text = "Archive opened. Select an entry to view, extract, or replace it.";
        RefreshEntries();
        UpdateButtons();
    }

    private async void OnOpenAnother(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PSARC archive",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PlayStation archive") { Patterns = ["*.psarc"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try { OpenArchive(path); }
        catch (Exception exception) { await ShowError("Could not open PSARC archive", exception); }
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => RefreshEntries();

    private void RefreshEntries()
    {
        if (_archive is null) { _grid.ItemsSource = null; return; }
        string query = _filter.Text?.Trim() ?? string.Empty;
        _grid.ItemsSource = _archive.Entries
            .Where(entry => query.Length == 0 || entry.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(entry => PsarcDisplayEntry.From(entry, _replacementPaths.GetValueOrDefault(entry)))
            .ToList();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (Selected is { } selected)
            _status.Text = $"{selected.Path} · {selected.Size} uncompressed · {selected.StoredSize} stored";
    }

    private async void OnView(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out PsarcEntry entry) || _archivePath is null || _archive is null) return;
        try
        {
            byte[] data;
            if (_replacements.TryGetValue(entry, out PsarcReplacement? replacement))
            {
                if (replacement.Length > 64 * 1024 * 1024)
                    throw new InvalidOperationException("The queued replacement is larger than 64 MiB. Extract or open the source file directly instead.");
                using Stream replacementStream = replacement.OpenRead();
                using var memory = new MemoryStream(checked((int)replacement.Length));
                await replacementStream.CopyToAsync(memory);
                data = memory.ToArray();
            }
            else
            {
                SetBusy(true, $"Reading {entry.Path}…", indeterminate: true);
                data = await Task.Run(() =>
                {
                    using var source = File.OpenRead(_archivePath);
                    return PsarcReader.ExtractEntryBytes(source, _archive, entry, cancellationToken: _cancellation!.Token);
                });
                SetBusy(false, $"Opened {entry.Path}.");
            }
            await new FileViewerDialog(Path.GetFileName(entry.Path), data).ShowDialog(this);
        }
        catch (OperationCanceledException) { SetBusy(false, "Preview cancelled."); }
        catch (Exception exception) { SetBusy(false, "Preview failed."); await ShowError("Could not preview PSARC entry", exception); }
    }

    private async void OnExtract(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out PsarcEntry entry) || _archivePath is null || _archive is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Extract {entry.Path}",
            SuggestedFileName = Path.GetFileName(entry.Path),
        });
        if (file?.TryGetLocalPath() is not { } destination) return;
        string temporary = destination + $".{Guid.NewGuid():N}.pkglens-part";
        try
        {
            SetBusy(true, $"Extracting {entry.Path}…");
            IProgress<PsarcProgress> operationProgress = CreateProgress(entry.Path);
            await Task.Run(() =>
            {
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (_replacements.TryGetValue(entry, out PsarcReplacement? replacement))
                {
                    using Stream input = replacement.OpenRead();
                    input.CopyTo(output);
                }
                else
                {
                    using var source = File.OpenRead(_archivePath);
                    PsarcReader.ExtractEntry(source, _archive, entry, output, _cancellation!.Token, operationProgress);
                }
                _cancellation!.Token.ThrowIfCancellationRequested();
            });
            File.Move(temporary, destination, true);
            SetBusy(false, $"Extracted {entry.Path} → {destination}");
        }
        catch (OperationCanceledException) { if (File.Exists(temporary)) File.Delete(temporary); SetBusy(false, "Extraction cancelled."); }
        catch (Exception exception) { if (File.Exists(temporary)) File.Delete(temporary); SetBusy(false, "Extraction failed."); await ShowError("Could not extract PSARC entry", exception); }
    }

    private async void OnExtractAll(object? sender, RoutedEventArgs e)
    {
        if (_archivePath is null || _archive is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Extract PSARC archive into…",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } destination) return;
        try
        {
            SetBusy(true, "Extracting all PSARC entries…");
            IProgress<PsarcProgress> operationProgress = CreateProgress(null);
            await Task.Run(() => PsarcReader.ExtractAll(_archivePath, _archive, destination,
                _cancellation!.Token, operationProgress));
            SetBusy(false, $"Extracted {_archive.Entries.Count:n0} files → {destination}");
        }
        catch (OperationCanceledException) { SetBusy(false, "Extract all cancelled. Completed files were kept."); }
        catch (Exception exception) { SetBusy(false, "Extract all failed."); await ShowError("Could not extract PSARC archive", exception); }
    }

    private async void OnReplace(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out PsarcEntry entry)) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replace {entry.Path} with…",
            AllowMultiple = false,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            _replacements[entry] = PsarcReplacement.FromFile(path);
            _replacementPaths[entry] = path;
            RefreshEntries();
            SelectEntry(entry);
            _status.Text = $"Queued {Path.GetFileName(path)} for {entry.Path}. Rebuild an archive copy to apply it.";
        }
        catch (Exception exception) { await ShowError("Could not queue replacement", exception); }
    }

    private void OnClearReplacement(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out PsarcEntry entry)) return;
        _replacements.Remove(entry);
        _replacementPaths.Remove(entry);
        RefreshEntries();
        SelectEntry(entry);
        _status.Text = $"Removed the queued replacement for {entry.Path}.";
    }

    private async void OnRebuild(object? sender, RoutedEventArgs e)
    {
        if (_archivePath is null || _archive is null) return;
        string suggested = Path.GetFileNameWithoutExtension(_archivePath) + "-modified.psarc";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save rebuilt PSARC copy as…",
            SuggestedFileName = suggested,
            DefaultExtension = "psarc",
            FileTypeChoices = [new FilePickerFileType("PlayStation archive") { Patterns = ["*.psarc"] }],
        });
        if (file?.TryGetLocalPath() is not { } destination) return;
        bool rebuilt = false;
        try
        {
            SetBusy(true, "Rebuilding PSARC archive copy…");
            IProgress<PsarcProgress> operationProgress = CreateProgress(null);
            await Task.Run(() =>
            {
                PsarcWriter.Repack(_archivePath, _archive, _replacements, destination,
                    _cancellation!.Token, operationProgress);
                rebuilt = true;
                Dispatcher.UIThread.Post(() => _status.Text = "Rebuild complete. Verifying every entry…");
                PsarcVerifier.Verify(destination, _cancellation.Token, operationProgress);
            });
            SetBusy(false, $"Rebuilt and verified {Path.GetFileName(destination)}. The original archive is unchanged.");
        }
        catch (OperationCanceledException)
        {
            if (rebuilt && File.Exists(destination)) File.Delete(destination);
            SetBusy(false, "Rebuild cancelled; unverified output was removed.");
        }
        catch (Exception exception)
        {
            if (rebuilt && File.Exists(destination)) File.Delete(destination);
            SetBusy(false, "Rebuild or verification failed; unverified output was removed.");
            await ShowError("Could not rebuild PSARC archive", exception);
        }
    }

    private IProgress<PsarcProgress> CreateProgress(string? fallbackItem) => new Progress<PsarcProgress>(value =>
    {
        _progress.IsIndeterminate = value.Total <= 0;
        if (value.Total > 0) _progress.Value = value.Percent;
        string item = value.Item ?? fallbackItem ?? "archive";
        _status.Text = $"Processing {item} · {value.Percent:0}%";
    });

    private bool TryGetSelection(out PsarcEntry entry)
    {
        if (Selected?.Entry is { } selected)
        {
            entry = selected;
            return true;
        }
        entry = null!;
        return false;
    }

    private void SelectEntry(PsarcEntry entry)
    {
        _grid.SelectedItem = (_grid.ItemsSource as IEnumerable<PsarcDisplayEntry>)?.FirstOrDefault(row => row.Entry == entry);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        PsarcEntry? entry = Selected?.Entry;
        _view.IsEnabled = !busy && entry is not null;
        _extract.IsEnabled = !busy && entry is not null;
        _replace.IsEnabled = !busy && entry is not null;
        _clearReplacement.IsEnabled = !busy && entry is not null && _replacements.ContainsKey(entry);
        _extractAll.IsEnabled = !busy && _archive is not null;
        _rebuild.IsEnabled = !busy && _archive is not null;
        _close.IsEnabled = !busy;
        _grid.IsEnabled = !busy;
        _filter.IsEnabled = !busy;
    }

    private void SetBusy(bool busy, string status, bool indeterminate = false)
    {
        if (busy)
        {
            _cancellation = new CancellationTokenSource();
            _progress.Value = 0;
        }
        else
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
        _status.Text = status;
        _progress.IsVisible = busy;
        _progress.IsIndeterminate = busy && indeterminate;
        _cancel.IsVisible = busy;
        _cancel.IsEnabled = busy;
        UpdateButtons();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cancel.IsEnabled = false;
        _status.Text = "Cancelling at a safe block boundary…";
        _cancellation?.Cancel();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_cancellation is not null) return;
        Close();
    }

    private Task ShowError(string title, Exception exception) =>
        new ErrorDialog(new GuiErrorReport(title, exception.Message, exception.ToString())).ShowDialog(this);

    private static string FormatSize(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:n0} B" : $"{value:0.##} {units[unit]}";
    }

    private sealed record PsarcDisplayEntry(PsarcEntry Entry, string Path, string Size, string StoredSize,
        string Storage, string State)
    {
        public static PsarcDisplayEntry From(PsarcEntry entry, string? replacementPath) => new(
            entry,
            entry.Path,
            FormatSize(entry.Length),
            FormatSize(entry.StoredLength),
            entry.IsCompressed ? "Compressed" : "Stored",
            replacementPath is null ? "Original" : $"Replace: {System.IO.Path.GetFileName(replacementPath)}");
    }
}
