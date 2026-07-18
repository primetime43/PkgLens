using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class BatchCenterDialog : Window
{
    private readonly string? _keysDirectory;
    private readonly string? _rapDirectory;
    private readonly IReadOnlyList<OperationDefinition> _operations = OperationCatalog.BatchOperations;

    private string? _sourceDirectory;
    private string? _outputDirectory;
    private BatchManifest? _manifest;
    private CancellationTokenSource? _cancellation;
    private bool _loadingManifest;

    private TextBlock _sourceText = null!;
    private TextBlock _outputText = null!;
    private TextBlock _description = null!;
    private TextBlock _status = null!;
    private ComboBox _operationBox = null!;
    private CheckBox _recursive = null!;
    private DataGrid _grid = null!;
    private Button _prepare = null!;
    private Button _run = null!;
    private Button _retry = null!;
    private Button _cancel = null!;
    private Button _openOutput = null!;
    private ProgressBar _progress = null!;
    private Control _setup = null!;
    private Control _dropOverlay = null!;

    public BatchCenterDialog() : this(null, null, null) { }

    public BatchCenterDialog(string? keysDirectory, string? rapDirectory, string? sourceDirectory = null)
    {
        _keysDirectory = keysDirectory;
        _rapDirectory = rapDirectory;
        InitializeComponent();
        FindControls();
        _operationBox.ItemsSource = _operations;
        _operationBox.SelectedItem = OperationCatalog.ForBatch(BatchOperation.Verify);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: true);
        if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
            SetSource(sourceDirectory);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindControls()
    {
        _sourceText = this.FindControl<TextBlock>("SourceText")!;
        _outputText = this.FindControl<TextBlock>("OutputText")!;
        _description = this.FindControl<TextBlock>("OperationDescription")!;
        _status = this.FindControl<TextBlock>("StatusText")!;
        _operationBox = this.FindControl<ComboBox>("OperationBox")!;
        _recursive = this.FindControl<CheckBox>("RecursiveCheck")!;
        _grid = this.FindControl<DataGrid>("JobsGrid")!;
        _prepare = this.FindControl<Button>("PrepareButton")!;
        _run = this.FindControl<Button>("RunButton")!;
        _retry = this.FindControl<Button>("RetryButton")!;
        _cancel = this.FindControl<Button>("CancelButton")!;
        _openOutput = this.FindControl<Button>("OpenOutputButton")!;
        _progress = this.FindControl<ProgressBar>("BatchProgress")!;
        _setup = this.FindControl<Control>("SetupPanel")!;
        _dropOverlay = this.FindControl<Control>("DropOverlay")!;
    }

    private async void OnChooseSource(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder containing packages",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SetSource(path);
    }

    private async void OnChooseOutput(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the batch output folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _outputDirectory = path;
        _outputText.Text = path;
        ClearPreparedJobs("Output changed. Prepare jobs again.");
        UpdateButtons();
    }

    private void SetSource(string path)
    {
        _sourceDirectory = Path.GetFullPath(path);
        _sourceText.Text = _sourceDirectory;
        if (_outputDirectory is null)
        {
            _outputDirectory = Path.Combine(_sourceDirectory, "PkgLens Batch Output");
            _outputText.Text = _outputDirectory;
        }
        ClearPreparedJobs("Source selected. Choose an operation, then prepare jobs.");
        UpdateButtons();
    }

    private void OnOperationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_operationBox.SelectedItem is not OperationDefinition choice) return;
        _description.Text = choice.BatchDescription;
        if (_loadingManifest) return;
        ClearPreparedJobs("Operation changed. Prepare jobs again.");
        UpdateButtons();
    }

    private async void OnPrepare(object? sender, RoutedEventArgs e)
    {
        if (_sourceDirectory is null || _outputDirectory is null ||
            _operationBox.SelectedItem is not OperationDefinition choice || choice.BatchOperation is not { } batchOperation) return;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, "Scanning and classifying packages…", indeterminate: true);
        var discoveryProgress = new Progress<BatchDiscoveryProgress>(value =>
        {
            _progress.IsIndeterminate = false;
            _progress.Value = value.Total == 0 ? 100 : value.Completed * 100d / value.Total;
            _status.Text = $"Inspecting {value.Source} ({value.Completed}/{value.Total})…";
        });

        try
        {
            string source = _sourceDirectory;
            string output = _outputDirectory;
            bool recursive = _recursive.IsChecked == true;
            string? keysDirectory = _keysDirectory;
            _manifest = await Task.Run(() => BatchProcessor.Create(source, output, batchOperation,
                recursive, new FileKeyProvider(keysDirectory), cancellation.Token, discoveryProgress),
                cancellation.Token);
            RefreshRows();
            _status.Text = _manifest.Jobs.Count == 0
                ? "No .pkg files were found."
                : $"Prepared {_manifest.Jobs.Count} resumable job(s). Manifest: {Path.GetFileName(_manifest.ManifestPath)}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Text = "Preparation cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not prepare jobs: {ex.Message}";
            await ShowError("Batch preparation failed", ex);
        }
        finally
        {
            _cancellation = null;
            SetBusy(false, _status.Text ?? string.Empty);
        }
    }

    private async void OnLoadManifest(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a PkgLens batch manifest",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PkgLens batch manifest") { Patterns = [".pkglens-batch-*.json", "*.json"] },
            ],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            _manifest = BatchProcessor.Load(path);
            _sourceDirectory = _manifest.SourceDirectory;
            _outputDirectory = _manifest.OutputDirectory;
            _sourceText.Text = _sourceDirectory;
            _outputText.Text = _outputDirectory;
            _recursive.IsChecked = _manifest.Recursive;
            _loadingManifest = true;
            try
            {
                _operationBox.SelectedItem = OperationCatalog.ForBatch(_manifest.Operation);
            }
            finally
            {
                _loadingManifest = false;
            }
            RefreshRows();
            _status.Text = $"Loaded {_manifest.Jobs.Count} jobs: {_manifest.CompletedCount} finished, " +
                           $"{_manifest.FailedCount} failed. Start / resume continues unfinished jobs.";
            UpdateButtons();
        }
        catch (Exception ex)
        {
            await ShowError("Could not load batch manifest", ex);
        }
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (_manifest is null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, "Running batch jobs…", indeterminate: false);
        var runProgress = new Progress<BatchRunProgress>(value =>
        {
            double item = (value.ItemPercent ?? 0) / 100d;
            _progress.Value = value.Total == 0 ? 100 : (value.Completed + item) * 100d / value.Total;
            _status.Text = $"{value.Source}: {value.Message} ({value.Completed}/{value.Total} finished)";
            RefreshRows();
        });

        try
        {
            BatchManifest manifest = _manifest;
            string? keysDirectory = _keysDirectory;
            string? rapDirectory = _rapDirectory;
            await Task.Run(() => BatchProcessor.Run(manifest, new FileKeyProvider(keysDirectory),
                new BatchProcessorOptions { RapDirectory = rapDirectory }, cancellation.Token, runProgress),
                cancellation.Token);
            RefreshRows();
            int completed = manifest.Jobs.Count(job => job.Status == BatchJobStatus.Completed);
            int skipped = manifest.Jobs.Count(job => job.Status == BatchJobStatus.Skipped);
            _status.Text = $"Batch finished: {completed} completed, {skipped} skipped, " +
                           $"{manifest.FailedCount} failed. Failed jobs can be retried after fixing keys or RAPs.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RefreshRows();
            _status.Text = "Batch paused. Progress was saved; Start / resume continues from this package.";
        }
        catch (Exception ex)
        {
            RefreshRows();
            _status.Text = $"Batch stopped: {ex.Message}";
            await ShowError("Batch processing stopped", ex);
        }
        finally
        {
            _cancellation = null;
            SetBusy(false, _status.Text ?? string.Empty);
        }
    }

    private void OnRetryFailed(object? sender, RoutedEventArgs e)
    {
        if (_manifest is null) return;
        BatchProcessor.RetryFailed(_manifest);
        RefreshRows();
        _status.Text = "Failed jobs are ready to retry. Press Start / resume.";
        UpdateButtons();
    }

    private void RefreshRows()
    {
        _grid.ItemsSource = _manifest?.Jobs.Select(BatchJobDisplayRow.From).ToList();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        _prepare.IsEnabled = !busy && _sourceDirectory is not null && _outputDirectory is not null;
        _run.IsEnabled = !busy && _manifest is { Jobs.Count: > 0 } &&
            _manifest.Jobs.Any(job => job.Status is BatchJobStatus.Pending or BatchJobStatus.Failed or BatchJobStatus.Running);
        _retry.IsEnabled = !busy && _manifest?.FailedCount > 0;
        _openOutput.IsEnabled = !busy && _outputDirectory is not null && Directory.Exists(_outputDirectory);
    }

    private void SetBusy(bool busy, string status, bool indeterminate = false)
    {
        _status.Text = status;
        _setup.IsEnabled = !busy;
        _grid.IsEnabled = !busy;
        _cancel.IsVisible = busy;
        _cancel.IsEnabled = busy;
        _progress.IsVisible = busy;
        _progress.IsIndeterminate = indeterminate;
        if (!busy) _progress.Value = 0;
        UpdateButtons();
    }

    private void ClearPreparedJobs(string status)
    {
        if (_manifest is null) return;
        _manifest = null;
        _grid.ItemsSource = null;
        _status.Text = status;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cancel.IsEnabled = false;
        _status.Text = "Pausing after the current safe checkpoint…";
        _cancellation?.Cancel();
    }

    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (_outputDirectory is null || !Directory.Exists(_outputDirectory)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _outputDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = ShowError("Could not open output folder", ex);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool hasFiles = e.DataTransfer.Formats.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        _dropOverlay.IsVisible = hasFiles;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => _dropOverlay.IsVisible = false;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dropOverlay.IsVisible = false;
        IStorageFolder? folder = e.DataTransfer.TryGetFiles()?.OfType<IStorageFolder>().FirstOrDefault();
        if (folder?.TryGetLocalPath() is { } path)
            SetSource(path);
        else
            _status.Text = "Drop a folder, not individual package files.";
    }

    private Task ShowError(string title, Exception exception) =>
        new ErrorDialog(new GuiErrorReport(title, exception.Message, exception.ToString())).ShowDialog(this);

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_cancellation is not null)
        {
            _status.Text = "Pause the batch before closing.";
            return;
        }
        Close();
    }
}


public sealed record BatchJobDisplayRow(
    string Status, string Package, string Title, string Platform, string Role,
    int Attempts, string Message, string Output)
{
    public static BatchJobDisplayRow From(BatchJob job) => new(
        job.Status.ToString(), job.RelativePath,
        job.Package.Title ?? job.Package.ContentId ?? (job.Package.Failed ? "(unreadable)" : "(unknown)"),
        job.Package.Platform, job.Package.Role.ToString(), job.Attempts,
        job.Message ?? string.Empty, job.OutputPath);
}
