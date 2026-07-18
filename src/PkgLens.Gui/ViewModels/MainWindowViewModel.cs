using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.ViewModels;

/// <summary>The tool pages shown in the left rail, in rail order.</summary>
public enum ToolPage { Home, Suggested, Package, Pack, Resign, Decrypt, Keys }

public sealed record GuiOperationProgress(string? Message = null, double? Percent = null);

public sealed record GuiErrorReport(string Title, string Message, string Details);

public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private PackageViewModel? _package;

    [ObservableProperty]
    private string _status = "Open or drag a .pkg file to begin.";

    [ObservableProperty]
    private string? _keysDirectory;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = "Working…";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private string _windowTitle = GuiVersion.ProductName;

    [ObservableProperty]
    private ToolPage _activeTool = ToolPage.Home;

    [ObservableProperty]
    private string _keyStatus = "";

    [ObservableProperty]
    private string? _rapDirectory = RapLibrarySettings.Load();

    [ObservableProperty]
    private string _rapStatus = "";

    private IReadOnlyList<string> _rapSearchDirectories = RapSearchFolderSettings.Load();

    private CancellationTokenSource? _operationCancellation;

    public ObservableCollection<RecentPackageItem> RecentPackages { get; } = new();
    public bool HasRecentPackages => RecentPackages.Count > 0;

    public event EventHandler<GuiErrorReport>? ErrorRequested;

    public MainWindowViewModel()
    {
        foreach (string path in RecentPackageSettings.Load())
            RecentPackages.Add(new RecentPackageItem(path));
        RefreshKeyStatus();
        RefreshRapStatus();
    }

    /// <summary>Two-way bridge for the rail ListBox's SelectedIndex.</summary>
    public int SelectedToolIndex
    {
        get => (int)ActiveTool;
        set { if (value >= 0) ActiveTool = (ToolPage)value; }
    }

    public bool IsHomeTool => ActiveTool == ToolPage.Home;
    public bool IsSuggestedTool => ActiveTool == ToolPage.Suggested;
    public bool IsPackageTool => ActiveTool == ToolPage.Package;
    public bool IsPackTool => ActiveTool == ToolPage.Pack;
    public bool IsResignTool => ActiveTool == ToolPage.Resign;
    public bool IsDecryptTool => ActiveTool == ToolPage.Decrypt;
    public bool IsKeysTool => ActiveTool == ToolPage.Keys;

    partial void OnActiveToolChanged(ToolPage value)
    {
        OnPropertyChanged(nameof(SelectedToolIndex));
        OnPropertyChanged(nameof(IsHomeTool));
        OnPropertyChanged(nameof(IsSuggestedTool));
        OnPropertyChanged(nameof(IsPackageTool));
        OnPropertyChanged(nameof(IsPackTool));
        OnPropertyChanged(nameof(IsResignTool));
        OnPropertyChanged(nameof(IsDecryptTool));
        OnPropertyChanged(nameof(IsKeysTool));
    }

    /// <summary>Updates the status-bar key indicator: the bundled key, or an override file if present.</summary>
    public void RefreshKeyStatus()
    {
        var provider = new FileKeyProvider(KeysDirectory);
        KeyStatus = provider.TryLocateKeyFile(out string path)
            ? $"key: {Path.GetFileName(path)} (override)"
            : "key: built-in";
    }

    public bool HasPackage => Package is not null;
    public bool IsNotBusy => !IsBusy;
    public string RapDirectoryDisplay => RapStore.DirectoryPath(RapDirectory);
    public IReadOnlyList<string> RapSearchDirectories => _rapSearchDirectories;
    public bool HasRapSearchDirectories => _rapSearchDirectories.Count > 0;
    public string RapSearchDirectoriesDisplay => _rapSearchDirectories.Count == 0
        ? "Only the folder beside the input will be searched."
        : string.Join("  •  ", _rapSearchDirectories);

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    partial void OnPackageChanged(PackageViewModel? oldValue, PackageViewModel? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(HasPackage));
        WindowTitle = newValue is null
            ? GuiVersion.ProductName
            : $"{GuiVersion.ProductName} — {newValue.Title}" +
              (string.IsNullOrEmpty(newValue.TitleId) ? "" : $" ({newValue.TitleId})");
    }

    partial void OnKeysDirectoryChanged(string? value)
    {
        Status = string.IsNullOrEmpty(value)
            ? "Keys directory cleared (using $PKGLENS_KEYS / ~/.pkglens)."
            : $"Keys directory set: {value}";
        RefreshKeyStatus();
    }

    partial void OnRapDirectoryChanged(string? value)
    {
        RapLibrarySettings.Save(value);
        OnPropertyChanged(nameof(RapDirectoryDisplay));
        RefreshRapStatus();
        Status = $"RAP library set: {RapStore.DirectoryPath(value)}";
    }

    public void RefreshRapStatus()
    {
        try
        {
            var entries = RapStore.List(RapDirectory);
            RapStatus = $"RAP library: {entries.Count(entry => entry.IsValid)} valid" +
                        (entries.Any(entry => !entry.IsValid)
                            ? $", {entries.Count(entry => !entry.IsValid)} invalid"
                            : string.Empty);
        }
        catch (Exception ex)
        {
            RapStatus = $"RAP library unavailable: {ex.Message}";
        }
    }

    public void SetRapSearchDirectories(IEnumerable<string> directories)
    {
        _rapSearchDirectories = directories.Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        RapSearchFolderSettings.Save(_rapSearchDirectories);
        OnPropertyChanged(nameof(RapSearchDirectories));
        OnPropertyChanged(nameof(HasRapSearchDirectories));
        OnPropertyChanged(nameof(RapSearchDirectoriesDisplay));
        Status = _rapSearchDirectories.Count == 0
            ? "Nearby RAP discovery will search beside each input."
            : $"Nearby RAP discovery will also search {_rapSearchDirectories.Count} selected folder(s).";
    }

    /// <summary>Closes the current package (disposing it) and returns to the empty state.</summary>
    public void CloseFile()
    {
        if (Package is null)
            return;
        Package = null; // OnPackageChanged disposes it and resets the window title
        Status = "Open or drag a .pkg file to begin.";
    }

    public async Task LoadRecentAsync(RecentPackageItem recent)
    {
        if (!File.Exists(recent.FilePath))
        {
            RemoveRecentPackage(recent.FilePath);
            ReportError("Recent package not found",
                new FileNotFoundException($"The package no longer exists: {recent.FilePath}", recent.FilePath));
            return;
        }

        await LoadAsync(recent.FilePath);
    }

    public void ClearRecentPackages()
    {
        RecentPackages.Clear();
        RecentPackageSettings.Save(Array.Empty<string>());
        OnPropertyChanged(nameof(HasRecentPackages));
        Status = "Recent package history cleared.";
    }

    private void RecordRecentPackage(string path)
    {
        string fullPath = Path.GetFullPath(path);
        RecentPackageItem? existing = RecentPackages.FirstOrDefault(item =>
            RecentPackageSettings.PathComparer.Equals(item.FilePath, fullPath));
        if (existing is not null)
            RecentPackages.Remove(existing);

        RecentPackages.Insert(0, new RecentPackageItem(fullPath));
        while (RecentPackages.Count > RecentPackageSettings.MaximumCount)
            RecentPackages.RemoveAt(RecentPackages.Count - 1);

        RecentPackageSettings.Save(RecentPackages.Select(item => item.FilePath));
        OnPropertyChanged(nameof(HasRecentPackages));
    }

    private void RemoveRecentPackage(string path)
    {
        RecentPackageItem? existing = RecentPackages.FirstOrDefault(item =>
            RecentPackageSettings.PathComparer.Equals(item.FilePath, path));
        if (existing is not null)
            RecentPackages.Remove(existing);
        RecentPackageSettings.Save(RecentPackages.Select(item => item.FilePath));
        OnPropertyChanged(nameof(HasRecentPackages));
    }

    public void CancelOperation()
    {
        if (_operationCancellation is { IsCancellationRequested: false })
        {
            BusyMessage = "Cancelling…";
            CanCancel = false;
            _operationCancellation.Cancel();
        }
    }

    public async Task RunOperationAsync(
        string message,
        string errorTitle,
        Func<CancellationToken, IProgress<GuiOperationProgress>, Task> operation,
        bool canCancel = true)
    {
        if (IsBusy)
            return;

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        BusyMessage = message;
        Status = message;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        CanCancel = canCancel;

        var progress = new Progress<GuiOperationProgress>(UpdateProgress);
        try
        {
            await operation(cancellation.Token, progress);
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = $"{message.TrimEnd('…', '.', ' ')} cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"{errorTitle}: {ex.Message}";
            ErrorRequested?.Invoke(this, new GuiErrorReport(errorTitle, ex.Message, ex.ToString()));
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
                _operationCancellation = null;
            CanCancel = false;
            IsBusy = false;
            IsProgressIndeterminate = true;
            ProgressValue = 0;
        }
    }

    public void ReportError(string title, Exception exception)
    {
        Status = $"{title}: {exception.Message}";
        ErrorRequested?.Invoke(this, new GuiErrorReport(title, exception.Message, exception.ToString()));
    }

    private void UpdateProgress(GuiOperationProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            BusyMessage = progress.Message;
            Status = progress.Message;
        }

        if (progress.Percent is { } percent)
        {
            ProgressValue = Math.Clamp(percent, 0, 100);
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Loads a package off the UI thread and swaps it in (disposing any prior one).</summary>
    public async Task LoadAsync(string path)
    {
        string fileName = Path.GetFileName(path);
        await RunOperationAsync($"Opening {fileName}…", "Package open failed", async (token, _) =>
        {
            IKeyProvider keys = new FileKeyProvider(KeysDirectory);
            var vm = await Task.Run(() => PackageViewModel.Load(path, keys, token), token);
            Package = vm;
            RecordRecentPackage(path);
            ActiveTool = ToolPage.Suggested; // show the package-aware next steps first
            Status = vm.IsDecrypted
                ? vm.StatusCounts
                : "Header only — this package's contents could not be decrypted.";
        });
    }
}
