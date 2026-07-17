using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PkgLens.Core.Shared;

namespace PkgLens.Gui.Views;

public partial class OperationPreflightDialog : Window
{
    private Button _continueButton = null!;

    public OperationPreflightDialog() : this(new OperationPreflightReport
    {
        Operation = "Operation",
        InputPath = string.Empty,
        OutputPath = string.Empty,
    }) { }

    public OperationPreflightDialog(OperationPreflightReport report)
    {
        InitializeComponent();
        _continueButton = this.FindControl<Button>("ContinueButton")!;
        this.FindControl<TextBlock>("OperationText")!.Text = report.Operation;
        this.FindControl<TextBlock>("SummaryText")!.Text =
            "PkgLens checked the input, keys, licenses, encryption support, output path, and available disk space.";
        this.FindControl<SelectableTextBlock>("InputText")!.Text = report.InputPath;
        this.FindControl<SelectableTextBlock>("OutputText")!.Text = report.OutputPath;
        this.FindControl<TextBlock>("ExpectedText")!.Text = report.ExpectedOutputBytes is long expected
            ? OperationPreflight.FormatBytes(expected)
            : "Unknown";
        this.FindControl<ItemsControl>("Checks")!.ItemsSource = report.Checks.Select(PreflightCheckRow.From).ToList();
        _continueButton.IsEnabled = report.CanProceed;
        this.FindControl<TextBlock>("FooterText")!.Text = report.CanProceed
            ? report.WarningCount == 0
                ? "All checks passed."
                : $"{report.WarningCount} warning(s). Review them before continuing."
            : $"{report.ErrorCount} blocking error(s) must be fixed before this operation can run.";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    private void OnContinue(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

public sealed record PreflightCheckRow(string Icon, string Name, string Status, string Details, IBrush Color)
{
    public static PreflightCheckRow From(PreflightCheck check) => check.Status switch
    {
        PreflightCheckStatus.Pass => new("✓", check.Name, "PASS", check.Details, Brushes.ForestGreen),
        PreflightCheckStatus.Warning => new("⚠", check.Name, "WARNING", check.Details, Brushes.DarkOrange),
        _ => new("✕", check.Name, "BLOCKED", check.Details, Brushes.Firebrick),
    };
}
