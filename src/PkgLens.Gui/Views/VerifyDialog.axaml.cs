using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class VerifyDialog : Window
{
    public VerifyDialog() => InitializeComponent();

    public VerifyDialog(PkgVerificationReport report)
    {
        InitializeComponent();

        var summary = this.FindControl<TextBlock>("Summary")!;
        var rows = this.FindControl<ItemsControl>("Rows")!;

        summary.Text = report.Passed
            ? "Integrity: OK — all checks passed."
            : $"Integrity: FAILED — {report.Failures} check(s) failed.";
        rows.ItemsSource = report.Checks.Select(CheckRow.From).ToList();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
