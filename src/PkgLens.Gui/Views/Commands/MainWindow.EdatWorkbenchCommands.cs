using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Interactivity;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    internal async void OnDiscoverDevKlic(object? sender, RoutedEventArgs e)
    {
        if (await new DevKlicDiscoveryDialog(Vm.RapDirectory).ShowDialog<bool>(this))
        { Vm.RefreshKlicenseeStatus(); Vm.Status = "Saved confirmed EDAT key to the local klicensee database."; }
    }

    internal async void OnEdatToolsClick(object? sender, RoutedEventArgs e)
    {
        await new EdatWorkbenchDialog(Vm.RapDirectory).ShowDialog(this);
        Vm.RefreshKlicenseeStatus();
    }

    internal async void OnRebuildSelectedEdat(object? sender, RoutedEventArgs e)
    {
        if (Vm.Package is not { SelectedItem: { IsDirectory: false, Entry: { } entry } } package) return;
        if (package.Operations.GetEntrySize(entry) > 128L * 1024 * 1024)
        {
            Vm.Status = "Extract this file and use EDAT / SDAT tools; package editing is limited to 128 MiB per protected file.";
            return;
        }
        byte[]? data = null;
        await RunOperationAsync("Reading protected file…", "Could not read protected file", async (token, _) =>
            data = await Task.Run(() => package.Operations.ReadEntryBytes(entry), token));
        if (data is null) return;
        var dialog = new EdatWorkbenchDialog(Vm.RapDirectory);
        try
        {
            dialog.LoadPackageEntry(Path.GetFileName(entry.Name), data, package.FilePath);
            if (await dialog.ShowDialog<byte[]?>(this) is { } rebuilt)
            {
                package.Operations.ReplaceEntry(entry, rebuilt);
                Vm.Status = $"Staged verified rebuild of {entry.Name}. Review changes or save a package copy.";
            }
        }
        catch (Exception ex) { dialog.Close(); Vm.ReportError("EDAT / SDAT rebuild failed", ex); }
    }
}
