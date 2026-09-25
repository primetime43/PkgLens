using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private bool _closeAfterConfirmation;
    private bool _closeConfirmationOpen;

    private async void OnReviewChanges(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { } package)
            return;
        bool save = await new PendingChangesDialog { DataContext = package }.ShowDialog<bool>(this);
        if (save)
            await SavePackageCopyAsync();
    }

    private async Task<bool> ConfirmPackageChangesAsync(PackageViewModel package)
    {
        var choice = await new UnsavedChangesDialog { DataContext = package }
            .ShowDialog<UnsavedChangesChoice>(this);
        return choice switch
        {
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Save => await SavePackageCopyAsync(),
            _ => false,
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeAfterConfirmation || DataContext is not MainWindowViewModel model)
            return;
        if (model.IsBusy)
        {
            e.Cancel = true;
            model.Status = "Wait for the operation to finish, or cancel it before closing.";
            return;
        }
        if (model.Package is not { HasPendingChanges: true })
            return;
        e.Cancel = true;
        if (!_closeConfirmationOpen)
            _ = CloseAfterConfirmationAsync();
    }

    private async Task CloseAfterConfirmationAsync()
    {
        _closeConfirmationOpen = true;
        try
        {
            if (await Vm.ConfirmPendingChangesAsync())
            {
                _closeAfterConfirmation = true;
                Close();
            }
        }
        finally
        {
            _closeConfirmationOpen = false;
        }
    }
}
