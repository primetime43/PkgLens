using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async void OnBrowsePsarcClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PSARC archive",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PlayStation archive") { Patterns = ["*.psarc"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        await ShowPsarcBrowser(path);
    }

    private async Task ShowPsarcBrowser(string path)
    {
        try
        {
            await new PsarcBrowserDialog(path).ShowDialog(this);
        }
        catch (Exception exception)
        {
            await new ErrorDialog(new GuiErrorReport(
                "Could not open PSARC archive", exception.Message, exception.ToString())).ShowDialog(this);
        }
    }
}
