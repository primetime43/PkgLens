using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PkgLens.Core.Keys;

namespace PkgLens.Gui.ViewModels;

/// <summary>The tool pages shown in the left rail, in rail order.</summary>
public enum ToolPage { Package, Pack, Resign, Decrypt, Keys }

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
    private string _windowTitle = "PkgLens";

    [ObservableProperty]
    private ToolPage _activeTool = ToolPage.Package;

    [ObservableProperty]
    private string _keyStatus = "";

    public MainWindowViewModel() => RefreshKeyStatus();

    /// <summary>Two-way bridge for the rail ListBox's SelectedIndex.</summary>
    public int SelectedToolIndex
    {
        get => (int)ActiveTool;
        set { if (value >= 0) ActiveTool = (ToolPage)value; }
    }

    public bool IsPackageTool => ActiveTool == ToolPage.Package;
    public bool IsPackTool => ActiveTool == ToolPage.Pack;
    public bool IsResignTool => ActiveTool == ToolPage.Resign;
    public bool IsDecryptTool => ActiveTool == ToolPage.Decrypt;
    public bool IsKeysTool => ActiveTool == ToolPage.Keys;

    partial void OnActiveToolChanged(ToolPage value)
    {
        OnPropertyChanged(nameof(SelectedToolIndex));
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

    partial void OnPackageChanged(PackageViewModel? oldValue, PackageViewModel? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(HasPackage));
        WindowTitle = newValue is null
            ? "PkgLens"
            : $"PkgLens — {newValue.Title}" + (string.IsNullOrEmpty(newValue.TitleId) ? "" : $" ({newValue.TitleId})");
    }

    partial void OnKeysDirectoryChanged(string? value)
    {
        Status = string.IsNullOrEmpty(value)
            ? "Keys directory cleared (using $PKGLENS_KEYS / ~/.pkglens)."
            : $"Keys directory set: {value}";
        RefreshKeyStatus();
    }

    /// <summary>Closes the current package (disposing it) and returns to the empty state.</summary>
    public void CloseFile()
    {
        if (Package is null)
            return;
        Package = null; // OnPackageChanged disposes it and resets the window title
        Status = "Open or drag a .pkg file to begin.";
    }

    /// <summary>Loads a package off the UI thread and swaps it in (disposing any prior one).</summary>
    public async Task LoadAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = $"Opening {Path.GetFileName(path)}…";
        try
        {
            IKeyProvider keys = new FileKeyProvider(KeysDirectory);
            var vm = await Task.Run(() => PackageViewModel.Load(path, keys));
            Package = vm;
            Status = vm.IsDecrypted
                ? vm.StatusCounts
                : "Header only — this package's contents could not be decrypted.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to open {Path.GetFileName(path)}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
