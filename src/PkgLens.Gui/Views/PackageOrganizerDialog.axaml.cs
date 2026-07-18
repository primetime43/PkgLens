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
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class PackageOrganizerDialog : Window
{
    private readonly string? _keysDirectory;
    private readonly IReadOnlyList<string> _filters = ["All packages", "Duplicates only", "Superseded updates", "Ready to organize", "Problems / skipped"];
    private string? _sourceDirectory;
    private string? _outputDirectory;
    private PackageOrganizerPlan? _plan;
    private CancellationTokenSource? _cancellation;

    private TextBlock _sourceText = null!;
    private TextBlock _outputText = null!;
    private TextBlock _status = null!;
    private TextBlock _summary = null!;
    private TextBlock _actionHelp = null!;
    private Control _summaryPanel = null!;
    private Control _setup = null!;
    private Control _dropOverlay = null!;
    private CheckBox _recursive = null!;
    private RadioButton _copyMode = null!;
    private RadioButton _moveMode = null!;
    private ComboBox _filter = null!;
    private DataGrid _grid = null!;
    private Button _preview = null!;
    private Button _savePlan = null!;
    private Button _organize = null!;
    private Button _cancel = null!;
    private Button _openOutput = null!;
    private ProgressBar _progress = null!;

    public PackageOrganizerDialog() : this(null, null) { }

    public PackageOrganizerDialog(string? keysDirectory, string? sourceDirectory = null)
    {
        _keysDirectory = keysDirectory;
        InitializeComponent();
        FindControls();
        _filter.ItemsSource = _filters;
        _filter.SelectedIndex = 0;
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
        _status = this.FindControl<TextBlock>("StatusText")!;
        _summary = this.FindControl<TextBlock>("SummaryText")!;
        _actionHelp = this.FindControl<TextBlock>("ActionHelpText")!;
        _summaryPanel = this.FindControl<Control>("SummaryPanel")!;
        _setup = this.FindControl<Control>("SetupPanel")!;
        _dropOverlay = this.FindControl<Control>("DropOverlay")!;
        _recursive = this.FindControl<CheckBox>("RecursiveCheck")!;
        _copyMode = this.FindControl<RadioButton>("CopyMode")!;
        _moveMode = this.FindControl<RadioButton>("MoveMode")!;
        _filter = this.FindControl<ComboBox>("FilterBox")!;
        _grid = this.FindControl<DataGrid>("Grid")!;
        _preview = this.FindControl<Button>("PreviewButton")!;
        _savePlan = this.FindControl<Button>("SavePlanButton")!;
        _organize = this.FindControl<Button>("OrganizeButton")!;
        _cancel = this.FindControl<Button>("CancelButton")!;
        _openOutput = this.FindControl<Button>("OpenOutputButton")!;
        _progress = this.FindControl<ProgressBar>("Progress")!;
    }

    private async void OnChooseSource(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the package library to organize",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) SetSource(path);
    }

    private async void OnChooseOutput(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the organized library output folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _outputDirectory = Path.GetFullPath(path);
        _outputText.Text = _outputDirectory;
        ClearPlan("Output changed. Scan again to rebuild the plan.");
        UpdateButtons();
    }

    private void SetSource(string path)
    {
        _sourceDirectory = Path.GetFullPath(path);
        _sourceText.Text = _sourceDirectory;
        if (_outputDirectory is null)
        {
            _outputDirectory = Path.Combine(_sourceDirectory, "Organized Packages");
            _outputText.Text = _outputDirectory;
        }
        ClearPlan("Source selected. Scan to preview duplicates and destinations.");
        UpdateButtons();
    }

    private async void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (_sourceDirectory is null || _outputDirectory is null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, "Hashing and classifying packages…", indeterminate: true);
        var progress = new Progress<PackageOrganizerProgress>(value =>
        {
            _progress.IsIndeterminate = false;
            _progress.Value = value.Total == 0 ? 100 : value.Completed * 100d / value.Total;
            _status.Text = $"{value.Stage}: {value.Source} ({value.Completed}/{value.Total})";
        });
        try
        {
            string source = _sourceDirectory;
            string output = _outputDirectory;
            bool recursive = _recursive.IsChecked == true;
            string? keysDirectory = _keysDirectory;
            _plan = await Task.Run(() => PackageOrganizer.Analyze(source, output, recursive,
                new FileKeyProvider(keysDirectory), cancellation.Token, progress), cancellation.Token);
            RefreshRows();
            UpdateSummary();
            _status.Text = _plan.Items.Count == 0
                ? "No .pkg files were found."
                : $"Preview ready. Review {_plan.Items.Count} package(s), then choose Copy or Move.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Text = "Organizer scan cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Organizer scan failed: {ex.Message}";
            await ShowError("Organizer scan failed", ex);
        }
        finally
        {
            _cancellation = null;
            SetBusy(false, _status.Text ?? string.Empty);
        }
    }

    private async void OnOrganize(object? sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        PackageOrganizerMode mode = _moveMode.IsChecked == true ? PackageOrganizerMode.Move : PackageOrganizerMode.Copy;
        if (mode == PackageOrganizerMode.Move)
        {
            bool confirmed = await new ConfirmationDialog(
                "Move package files?",
                "PkgLens will copy and SHA-256 verify each package before removing its original. Exact duplicates remain untouched at their source locations.",
                "Move verified files").ShowDialog<bool>(this);
            if (!confirmed) return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, mode == PackageOrganizerMode.Move ? "Organizing and verifying moves…" : "Organizing and verifying copies…", false);
        var progress = new Progress<PackageOrganizerProgress>(value =>
        {
            double item = value.BytesTotal == 0 ? 0 : (double)value.BytesCompleted / value.BytesTotal;
            _progress.Value = value.Total == 0 ? 100 : (value.Completed + item) * 100d / value.Total;
            _status.Text = $"{value.Stage}: {value.Source} ({value.Completed}/{value.Total})";
            RefreshRows();
        });
        try
        {
            PackageOrganizerPlan plan = _plan;
            await Task.Run(() => PackageOrganizer.Apply(plan, mode, cancellation.Token, progress), cancellation.Token);
            RefreshRows();
            int failed = plan.Items.Count(item => item.Status == PackageOrganizerStatus.Failed);
            _status.Text = $"Organization finished: {plan.Items.Count(item => item.Status is PackageOrganizerStatus.Copied or PackageOrganizerStatus.Moved or PackageOrganizerStatus.AlreadyPresent)} organized, " +
                           $"{plan.ExactDuplicateCount} exact duplicate(s) left untouched, {failed} failed.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RefreshRows();
            _status.Text = "Organization cancelled. Completed verified files remain safe; preview again to continue.";
        }
        catch (Exception ex)
        {
            RefreshRows();
            _status.Text = $"Organization stopped: {ex.Message}";
            await ShowError("Package organization stopped", ex);
        }
        finally
        {
            _cancellation = null;
            SetBusy(false, _status.Text ?? string.Empty);
        }
    }

    private async void OnSavePlan(object? sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save package organizer plan",
            SuggestedFileName = "package-organizer-plan.json",
            DefaultExtension = "json",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            await AtomicOutput.WriteAllTextAsync(path, PackageOrganizer.ToJson(_plan));
            _status.Text = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            await ShowError("Could not save organizer plan", ex);
        }
    }

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) => RefreshRows();
    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_organize is not null) UpdateButtons();
    }

    private void RefreshRows()
    {
        if (_plan is null)
        {
            _grid.ItemsSource = null;
            return;
        }
        IEnumerable<PackageOrganizerItem> items = _plan.Items;
        items = _filter.SelectedIndex switch
        {
            1 => items.Where(item => item.IsExactDuplicate || item.IsContentIdDuplicate),
            2 => items.Where(item => item.IsSupersededUpdate),
            3 => items.Where(item => item.Action == PackageOrganizerAction.Organize),
            4 => items.Where(item => item.Action != PackageOrganizerAction.Organize || item.Status == PackageOrganizerStatus.Failed),
            _ => items,
        };
        _grid.ItemsSource = items.Select(PackageOrganizerDisplayRow.From).ToList();
        UpdateButtons();
    }

    private void UpdateSummary()
    {
        if (_plan is null) { _summaryPanel.IsVisible = false; return; }
        _summary.Text = $"{_plan.Items.Count} package(s) · {_plan.ExactDuplicateCount} exact duplicate(s) · " +
                        $"{_plan.ContentIdDuplicateCount} content-ID variant(s) · {_plan.SupersededUpdateCount} superseded update(s) · " +
                        $"{_plan.OrganizeCount} file(s) ready to organize · {_plan.UnreadableCount} unreadable";
        _summaryPanel.IsVisible = true;
    }

    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        _preview.IsEnabled = !busy && _sourceDirectory is not null && _outputDirectory is not null;
        _savePlan.IsEnabled = !busy && _plan is { Items.Count: > 0 };
        _organize.IsEnabled = !busy && _plan is { OrganizeCount: > 0 };
        bool move = _moveMode.IsChecked == true;
        _organize.Content = move ? "Move into organized library" : "Create organized copies";
        _actionHelp.Text = move
            ? "Move mode first copies each package to the displayed destination and verifies SHA-256. Only then is that original removed. Exact duplicates remain untouched."
            : "Create organized copies writes SHA-256-verified copies to the displayed destinations. Every original package remains unchanged.";
        ToolTip.SetTip(_organize, move
            ? "Copies and verifies each organized package, then removes its original. Exact duplicates are never removed automatically."
            : "Copies packages marked Organize into the displayed destinations and verifies every copied SHA-256 hash. Originals remain untouched.");
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

    private void ClearPlan(string status)
    {
        _plan = null;
        _grid.ItemsSource = null;
        _summaryPanel.IsVisible = false;
        _status.Text = status;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cancel.IsEnabled = false;
        _status.Text = "Cancelling at a safe checkpoint…";
        _cancellation?.Cancel();
    }

    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (_outputDirectory is null || !Directory.Exists(_outputDirectory)) return;
        try { Process.Start(new ProcessStartInfo { FileName = _outputDirectory, UseShellExecute = true }); }
        catch (Exception ex) { _ = ShowError("Could not open output folder", ex); }
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
        if (folder?.TryGetLocalPath() is { } path) SetSource(path);
        else _status.Text = "Drop a folder containing package files.";
    }

    private Task ShowError(string title, Exception exception) =>
        new ErrorDialog(new GuiErrorReport(title, exception.Message, exception.ToString())).ShowDialog(this);

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_cancellation is not null) { _status.Text = "Cancel the current operation before closing."; return; }
        Close();
    }
}

public sealed record PackageOrganizerDisplayRow(
    string Action, string Package, string Title, string Platform, string Role, string Version,
    string Relationship, string Result, string Destination)
{
    public static PackageOrganizerDisplayRow From(PackageOrganizerItem item) => new(
        item.Action switch
        {
            PackageOrganizerAction.Organize => "Organize",
            PackageOrganizerAction.SkipExactDuplicate => "Keep source",
            _ => "Skip",
        },
        item.RelativePath, item.Package.Title ?? item.Package.ContentId ?? "(unknown)",
        item.Package.Platform, item.Package.Role.ToString(), item.Package.Version ?? "—",
        item.Relationship, item.Result ?? item.Status.ToString(), item.DestinationPath ?? "—");
}
