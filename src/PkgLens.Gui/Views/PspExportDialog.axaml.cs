using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core.Psp;

namespace PkgLens.Gui.Views;

public partial class PspExportDialog : Window
{
    public PspExportDialog() => InitializeComponent();

    public PspExportDialog(string sourcePath)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("SourceText")!.Text = sourcePath;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        PspExportFormat format = this.FindControl<RadioButton>("PbpRadio")!.IsChecked == true
            ? PspExportFormat.Pbp
            : this.FindControl<RadioButton>("CsoRadio")!.IsChecked == true
                ? PspExportFormat.Cso
                : PspExportFormat.Iso;
        Close(format);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
