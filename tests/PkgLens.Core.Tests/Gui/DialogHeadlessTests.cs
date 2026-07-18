using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using PkgLens.Core.Shared;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public sealed class DialogHeadlessTests
{
    [AvaloniaFact]
    public void UpdateDialog_ShowsInstalledAndLatestVersionsWithReleaseLink()
    {
        var result = new UpdateCheckResult("1.0.0", "1.1.0",
            new Uri("https://github.com/primetime43/PkgLens/releases/tag/v1.1.0"), true);
        var dialog = new UpdateCheckDialog(result);

        Assert.Contains("newer", dialog.FindControl<TextBlock>("HeadingText")!.Text,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.0.0", dialog.FindControl<TextBlock>("CurrentVersionText")!.Text);
        Assert.Equal("1.1.0", dialog.FindControl<TextBlock>("LatestVersionText")!.Text);
        Assert.Equal(result.ReleaseUri, dialog.FindControl<HyperlinkButton>("ReleaseLink")!.NavigateUri);
        dialog.Close();
    }

    [AvaloniaFact]
    public void ErrorDialog_PreservesMessageScrollableDetailsAndUsableOkButton()
    {
        var report = new GuiErrorReport("Export failed", "The output could not be verified.", "technical details");
        var dialog = new ErrorDialog(report);

        Assert.Equal("Export failed", dialog.Title);
        Assert.Equal(report.Message, dialog.FindControl<TextBlock>("MessageText")!.Text);

        TextBox details = dialog.FindControl<TextBox>("DetailsText")!;
        Assert.Equal(report.Details, details.Text);
        Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetHorizontalScrollBarVisibility(details));
        Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(details));
        Assert.False(details.TextWrapping == Avalonia.Media.TextWrapping.Wrap);

        Button ok = dialog.FindControl<Button>("OkButton")!;
        Assert.True(ok.IsDefault);
        Assert.True(ok.MinWidth >= 88);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, ok.HorizontalContentAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, ok.VerticalContentAlignment);
        dialog.Close();
    }

    [AvaloniaFact]
    public void FirmwareAndBatchGrids_KeepResizableAccessibleColumns()
    {
        var firmware = new FirmwareAnalysisDialog(new FirmwareAnalysisReport { Source = "sample.pkg" }, false);
        DataGrid firmwareGrid = firmware.FindControl<DataGrid>("ItemsGrid")!;

        Assert.True(firmwareGrid.CanUserResizeColumns);
        Assert.True(firmwareGrid.CanUserSortColumns);
        Assert.True(firmwareGrid.CanUserReorderColumns);
        Assert.Equal(6, firmwareGrid.Columns.Count);
        Assert.All(firmwareGrid.Columns, column =>
        {
            TextBlock header = Assert.IsType<TextBlock>(column.Header);
            Assert.False(string.IsNullOrWhiteSpace(header.Text));
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(header)?.ToString()));
            Assert.True(column.MinWidth > 0);
        });

        var batch = new BatchCenterDialog();
        DataGrid batchGrid = batch.FindControl<DataGrid>("JobsGrid")!;
        Assert.True(batchGrid.CanUserResizeColumns);
        Assert.True(batchGrid.CanUserSortColumns);
        Assert.Equal(8, batchGrid.Columns.Count);
        Control targetPanel = batch.FindControl<Control>("TargetProfilePanel")!;
        ComboBox operationBox = batch.FindControl<ComboBox>("OperationBox")!;
        ComboBox targetBox = batch.FindControl<ComboBox>("TargetProfileBox")!;
        Assert.False(targetPanel.IsVisible);
        operationBox.SelectedItem = OperationCatalog.ForBatch(BatchOperation.ConvertCfw);
        Assert.True(targetPanel.IsVisible);
        Assert.Equal(3, targetBox.ItemCount);
        Assert.Equal(TargetCompatibilityProfile.CexCfw,
            Assert.IsType<TargetCompatibilityProfileInfo>(targetBox.SelectedItem).Profile);
        Assert.False(string.IsNullOrWhiteSpace(
            batch.FindControl<TextBlock>("TargetProfileDescription")!.Text));
        firmware.Close();
        batch.Close();
    }
}
