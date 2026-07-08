using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PkgLens.Core.Keys;

namespace PkgLens.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private PackageViewModel? _package;

    [ObservableProperty]
    private string _status = "Open a .pkg file to begin.";

    [ObservableProperty]
    private string? _keysDirectory;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _windowTitle = "PkgLens";

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
                : "Header only — a retail key is required to list the contents.";
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
