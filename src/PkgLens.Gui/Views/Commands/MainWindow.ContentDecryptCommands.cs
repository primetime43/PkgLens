using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PkgLens.Core.Shared;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    internal async void OnDecryptPackageContentsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.IsBusy || Vm.Package is not { IsDecrypted: true } package) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Decrypt package contents — choose where to create the output folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } parent) return;
        string stem = Path.GetFileNameWithoutExtension(package.FilePath) + "-decrypted";
        string destination = Path.Combine(parent, stem);
        for (int suffix = 2; Path.Exists(destination); suffix++)
            destination = Path.Combine(parent, $"{stem}-{suffix}");

        ContentDecryptReport? report = null;
        await RunOperationAsync("Decrypting package contents…", "Package content export failed", async (token, progress) =>
        {
            var contentProgress = new Progress<ContentDecryptProgress>(value =>
                progress.Report(new GuiOperationProgress(value.Message, value.Percent)));
            report = await Task.Run(() => package.Operations.DecryptContents(destination,
                new ContentDecryptOptions { RapDirectory = Vm.RapDirectory }, token, contentProgress), token);
        });
        if (report is null) return;
        Vm.Status = $"{report.Summary} → {destination}";
        await new ContentDecryptReportDialog(report).ShowDialog(this);
    }
}
