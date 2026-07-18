using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core.Ps1;

namespace PkgLens.Gui.Views;

public sealed record Ps1ClassicExportDialogResult(bool ReconstructBinCue);

public partial class Ps1ClassicExportDialog : Window
{
    public Ps1ClassicExportDialog() => InitializeComponent();

    public Ps1ClassicExportDialog(string sourcePath, Ps1ClassicExportEligibility eligibility)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("SourceText")!.Text = sourcePath;
        this.FindControl<TextBlock>("DetectionText")!.Text = eligibility.ImageKind switch
        {
            Ps1ClassicImageKind.MultiDisc => "Multi-disc PS1 Classic detected",
            Ps1ClassicImageKind.SingleDisc => "Single-disc PS1 Classic detected",
            _ => "Licensed PS1 Classic image detected",
        };
        this.FindControl<TextBlock>("LicenseText")!.Text = eligibility.RapRequired
            ? $"License: matching RAP ready for {eligibility.LicenseContentId}."
            : "License: no additional RAP is required for extraction.";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnExport(object? sender, RoutedEventArgs e) =>
        Close(new Ps1ClassicExportDialogResult(this.FindControl<CheckBox>("ReconstructCheck")!.IsChecked == true));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
