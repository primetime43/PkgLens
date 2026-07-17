using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Vita;

namespace PkgLens.Gui.Views;

public sealed record VitaExportDialogResult(string? WorkBinPath);

public partial class VitaExportDialog : Window
{
    private string? _workBinPath;

    public VitaExportDialog() => InitializeComponent();

    public VitaExportDialog(string sourcePath, VitaPackageDetails details)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("SourceText")!.Text = sourcePath;
        this.FindControl<TextBlock>("PackageText")!.Text = $"{details.Kind} · {details.Title} ({details.TitleId})";
        this.FindControl<TextBlock>("MetadataText")!.Text =
            $"Key revision {details.KeyRevision} · category {details.Category ?? "unknown"} · " +
            $"app {details.AppVersion ?? "unknown"} · firmware {details.MinimumFirmware ?? "unknown"}";
        this.FindControl<TextBlock>("RootText")!.Text = details.RelativeRoot;
        this.FindControl<TextBlock>("LicenseNote")!.Text = details.RequiresLicense
            ? "No license is bundled. Select the matching 512-byte work.bin/RIF, or export without it and add it later."
            : "This update/free package does not require a work.bin for this export step.";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnBrowseLicense(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Vita work.bin / RIF license",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Vita license") { Patterns = new[] { "work.bin", "*.rif", "*.bin" } },
                FilePickerFileTypes.All,
            },
        });
        _workBinPath = files.FirstOrDefault()?.TryGetLocalPath();
        if (_workBinPath is not null)
            this.FindControl<TextBox>("LicensePathBox")!.Text = _workBinPath;
    }

    private void OnExport(object? sender, RoutedEventArgs e) => Close(new VitaExportDialogResult(_workBinPath));
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
