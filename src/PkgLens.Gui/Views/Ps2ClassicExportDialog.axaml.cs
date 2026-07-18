using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core.Ps2;

namespace PkgLens.Gui.Views;

public sealed record Ps2ClassicExportDialogResult(bool RebuildCfwPackage);

public partial class Ps2ClassicExportDialog : Window
{
    public Ps2ClassicExportDialog() => InitializeComponent();

    public Ps2ClassicExportDialog(string sourcePath, Ps2ClassicExportEligibility eligibility)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("SourceText")!.Text = sourcePath;
        this.FindControl<TextBlock>("DetailsText")!.Text =
            $"Content ID: {eligibility.ContentId} · ISO size: {FormatBytes(eligibility.IsoSize)} · " +
            (eligibility.RapRequired ? "License: matching RAP resolved" : "License: built-in key");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnContinue(object? sender, RoutedEventArgs e) => Close(
        new Ps2ClassicExportDialogResult(this.FindControl<RadioButton>("CfwRadio")!.IsChecked == true));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.##} GB"
        : $"{bytes / (1024d * 1024):0.##} MB";
}
