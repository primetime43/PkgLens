using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PkgLens.Core.Shared;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public sealed class PreflightDialogHeadlessTests
{
    [AvaloniaFact]
    public void BlockingError_DisablesContinueAndExplainsWhy()
    {
        var report = Report(new PreflightCheck("License / RAP", PreflightCheckStatus.Error, "Matching RAP is missing."));
        var dialog = new OperationPreflightDialog(report);

        Assert.False(dialog.FindControl<Button>("ContinueButton")!.IsEnabled);
        Assert.Contains("1 blocking error", dialog.FindControl<TextBlock>("FooterText")!.Text);
        PreflightCheckRow row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<PreflightCheckRow>>(
            dialog.FindControl<ItemsControl>("Checks")!.ItemsSource));
        Assert.Equal("BLOCKED", row.Status);
        Assert.Equal("License / RAP", row.Name);
        dialog.Close();
    }

    [AvaloniaFact]
    public void Warning_AllowsContinueAndRequiresReview()
    {
        var report = Report(new PreflightCheck("Existing output", PreflightCheckStatus.Warning, "The output will be replaced."));
        var dialog = new OperationPreflightDialog(report);

        Assert.True(dialog.FindControl<Button>("ContinueButton")!.IsEnabled);
        Assert.Contains("1 warning", dialog.FindControl<TextBlock>("FooterText")!.Text);
        PreflightCheckRow row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<PreflightCheckRow>>(
            dialog.FindControl<ItemsControl>("Checks")!.ItemsSource));
        Assert.Equal("WARNING", row.Status);
        dialog.Close();
    }

    private static OperationPreflightReport Report(params PreflightCheck[] checks) => new()
    {
        Operation = "Export package",
        InputPath = "input.pkg",
        OutputPath = "output.iso",
        ExpectedOutputBytes = 1024,
        Checks = checks,
    };
}
