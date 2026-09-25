using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _confirmingChanges;

    // The host supplies the dialog; absence of a host must never silently discard edits.
    public Func<PackageViewModel, Task<bool>>? ConfirmPackageChanges { get; set; }

    public async Task<bool> ConfirmPendingChangesAsync()
    {
        if (IsBusy || _confirmingChanges)
            return false;
        if (Package is not { HasPendingChanges: true } package)
            return true;
        if (ConfirmPackageChanges is null)
            return false;
        _confirmingChanges = true;
        try
        {
            return await ConfirmPackageChanges(package);
        }
        finally
        {
            _confirmingChanges = false;
        }
    }

    private void OnPackageEditStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageViewModel.HasPendingChanges))
            UpdateWindowTitle();
    }

    private void UpdateWindowTitle() => WindowTitle = Package is not { } package
        ? GuiVersion.ProductName
        : $"{GuiVersion.ProductName} — {package.Title}" +
          (string.IsNullOrEmpty(package.TitleId) ? "" : $" ({package.TitleId})") +
          (package.HasPendingChanges ? " • Unsaved changes" : "");

    public async Task<bool> SavePackageAsAsync(string destination)
    {
        if (IsBusy || Package is not { } package)
            return false;

        bool saved = false;
        await RunOperationAsync("Repacking…", "Save failed", async (token, progress) =>
        {
            var packageProgress = new Progress<PkgOperationProgress>(value =>
                progress.Report(PackagePresentationService.ToGuiProgress(value)));
            var keys = new FileKeyProvider(KeysDirectory);
            PackageViewModel replacement = await Task.Run(() =>
            {
                package.Operations.SaveAs(destination, token, packageProgress);
                progress.Report(new GuiOperationProgress("Verifying rebuilt package…"));
                PostOperationVerifier.VerifyPackage(destination, keys);
                return PackageViewModel.Load(destination, keys, token);
            }, token);
            if (token.IsCancellationRequested)
            {
                replacement.Dispose();
                token.ThrowIfCancellationRequested();
            }

            // Switch to the verified copy so future edits/reverts use the saved contents.
            // A failed or cancelled save leaves the original session and edits intact.
            Package = replacement;
            RecordRecentPackage(destination);
            Status = $"Saved and verified {Path.GetFileName(destination)}. The saved copy is now open." +
                     (replacement.IsRetail ? " Retail output remains unsigned for stock consoles." : "");
            saved = true;
        });
        return saved;
    }
}
