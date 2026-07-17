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
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class KeyLicenseAuditDialog : Window
{
    private readonly string? _keysDirectory;
    private string? _source;
    private KeyLicenseAuditReport? _report;
    private CancellationTokenSource? _cancellation;
    private readonly bool _autoStart;

    private StackPanel _inputPanel = null!;
    private TextBlock _sourceText = null!;
    private TextBlock _statusText = null!;
    private Button _auditButton = null!;
    private Button _saveJsonButton = null!;
    private Button _cancelButton = null!;
    private ProgressBar _progress = null!;
    private DataGrid _grid = null!;

    public KeyLicenseAuditDialog() : this(null, null) { }

    public KeyLicenseAuditDialog(string? keysDirectory) : this(keysDirectory, null) { }

    public KeyLicenseAuditDialog(string? keysDirectory, string? source, bool autoStart = false)
    {
        InitializeComponent();
        _keysDirectory = keysDirectory;
        _autoStart = autoStart;
        _inputPanel = this.FindControl<StackPanel>("InputPanel")!;
        _sourceText = this.FindControl<TextBlock>("SourceText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _auditButton = this.FindControl<Button>("AuditBtn")!;
        _saveJsonButton = this.FindControl<Button>("SaveJsonBtn")!;
        _cancelButton = this.FindControl<Button>("CancelBtn")!;
        _progress = this.FindControl<ProgressBar>("AuditProgress")!;
        _grid = this.FindControl<DataGrid>("Grid")!;
        if (!string.IsNullOrWhiteSpace(source))
            SelectSource(source);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a package or protected-content file to audit",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Packages and protected content")
                {
                    Patterns = new[] { "*.pkg", "EBOOT.BIN", "*.self", "*.sprx", "*.edat", "*.sdat", "*.pgd", "*.elf" },
                },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SelectSource(path);
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to audit recursively",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SelectSource(path);
    }

    private void SelectSource(string path)
    {
        _source = path;
        _sourceText.Text = path;
        _auditButton.IsEnabled = true;
        _statusText.Text = Directory.Exists(path) ? "Ready to audit this folder recursively." : "Ready to audit this file.";
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_autoStart && _source is not null)
            await RunAuditAsync();
    }

    private async void OnAudit(object? sender, RoutedEventArgs e) => await RunAuditAsync();

    private async Task RunAuditAsync()
    {
        if (_source is null)
            return;

        string source = _source;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, "Auditing…", indeterminate: true);
        IProgress<KeyLicenseAuditProgress> progress = new Progress<KeyLicenseAuditProgress>(value =>
        {
            _progress.IsIndeterminate = false;
            _progress.Value = value.Total == 0 ? 100 : value.Completed * 100d / value.Total;
            _statusText.Text = $"Auditing {value.Source} ({value.Completed}/{value.Total})…";
        });

        try
        {
            KeyLicenseAuditReport report = await Task.Run(() => KeyLicenseAudit.Inspect(source,
                new FileKeyProvider(_keysDirectory), cancellationToken: cancellation.Token, progress: progress),
                cancellation.Token);
            _report = report;
            _grid.ItemsSource = report.Items.Select(AuditResultRow.From).ToList();
            _statusText.Text = $"{report.Items.Count} item(s) — {report.MissingRapCount} missing RAP(s), " +
                               $"{report.UnsupportedCount} unsupported, {report.ErrorCount} error(s).";
            _saveJsonButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _statusText.Text = "Audit cancelled.";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Audit failed: {ex.Message}";
            await ShowError("Key / license audit failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
                _cancellation = null;
            SetBusy(false, _statusText.Text ?? string.Empty);
        }
    }

    private async void OnSaveJson(object? sender, RoutedEventArgs e)
    {
        if (_report is null)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save key / license audit as JSON…",
            SuggestedFileName = "key-license-audit.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file?.TryGetLocalPath() is not { } destination)
            return;

        try
        {
            await AtomicOutput.WriteAllTextAsync(destination, KeyLicenseAudit.ToJson(_report), CancellationToken.None);
            _statusText.Text = $"Saved {Path.GetFileName(destination)}.";
        }
        catch (Exception ex)
        {
            await ShowError("Save audit failed", ex);
        }
    }

    private void SetBusy(bool busy, string status, bool indeterminate = false)
    {
        _statusText.Text = status;
        _inputPanel.IsEnabled = !busy;
        _grid.IsEnabled = !busy;
        _saveJsonButton.IsEnabled = !busy && _report is not null;
        _cancelButton.IsVisible = busy;
        _cancelButton.IsEnabled = busy;
        _progress.IsVisible = busy;
        _progress.IsIndeterminate = indeterminate;
        if (!busy)
            _progress.Value = 0;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cancelButton.IsEnabled = false;
        _statusText.Text = "Cancelling…";
        _cancellation?.Cancel();
    }

    private async Task ShowError(string title, Exception exception) =>
        await new ErrorDialog(new GuiErrorReport(title, exception.Message, exception.ToString())).ShowDialog(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private sealed record AuditResultRow(string Source, string Kind, string RequiredKey, string KeyStatus,
        string SelfRevision, string ContentId, string LicenseType, string RapStatus, string EncryptionStatus,
        string Details)
    {
        public static AuditResultRow From(KeyLicenseAuditItem item) => new(
            item.Source,
            item.Kind.ToString(),
            item.RequiredKey ?? string.Empty,
            item.KeyStatus.ToString(),
            item.SelfRevision is ushort revision ? $"0x{revision:X4}" : string.Empty,
            item.ContentId ?? string.Empty,
            item.LicenseType ?? string.Empty,
            item.RapStatus.ToString(),
            item.EncryptionStatus.ToString(),
            item.Details ?? string.Empty);
    }
}
