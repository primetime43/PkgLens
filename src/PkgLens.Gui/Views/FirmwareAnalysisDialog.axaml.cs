using System;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Shared;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public sealed record FirmwarePatchRequest(CfwFirmwareTarget Target);

public partial class FirmwareAnalysisDialog : Window
{
    private readonly FirmwareAnalysisReport _report;
    private readonly bool _canPatchPackage;
    private readonly int _highestPatchableVersion;
    private TextBox _targetBox = null!;
    private TextBlock _patchError = null!;

    public FirmwareAnalysisDialog() : this(new FirmwareAnalysisReport { Source = string.Empty }, false) { }

    public FirmwareAnalysisDialog(FirmwareAnalysisReport report, bool canPatchPackage)
    {
        _report = report;
        _canPatchPackage = canPatchPackage && report.PatchableCount > 0;
        _highestPatchableVersion = report.Items.Where(item => item.CanPatch)
            .Select(item => item.RequiredVersion)
            .DefaultIfEmpty(0)
            .Max();
        InitializeComponent();
        _targetBox = this.FindControl<TextBox>("TargetBox")!;
        _patchError = this.FindControl<TextBlock>("PatchErrorText")!;
        _targetBox.Text = SuggestTarget(_highestPatchableVersion);
        _targetBox.Watermark = _highestPatchableVersion > 0
            ? $"Below {FirmwareAnalysisReport.FormatVersion(_highestPatchableVersion)}"
            : "Target";
        this.FindControl<TextBlock>("SummaryText")!.Text = !report.IsApplicable
            ? "PS3 firmware analysis does not apply to this package."
            : report.Items.Count == 0
                ? "No PS3 SELF/SPRX executables were found."
                : $"Highest required firmware: {report.HighestRequiredFirmware ?? "unknown"} · " +
                  $"{report.Items.Count} executable(s) · {report.FullyAnalyzedCount} fully analyzed · " +
                  $"{report.PatchableCount} ready to patch";
        this.FindControl<TextBlock>("SourceText")!.Text = report.Source;
        DataGrid itemsGrid = this.FindControl<DataGrid>("ItemsGrid")!;
        itemsGrid.ItemsSource = report.Items.Select(FirmwareAnalysisRow.From).ToList();
        itemsGrid.IsVisible = report.Items.Count > 0;
        this.FindControl<Border>("GuidancePanel")!.IsVisible = report.IsApplicable && report.Items.Count > 0;
        if (report.Items.Count == 0)
        {
            this.FindControl<Border>("EmptyPanel")!.IsVisible = true;
            this.FindControl<TextBlock>("EmptyTitleText")!.Text = report.IsApplicable
                ? "Nothing compatible was found"
                : "This tool is for PS3 executables";
            this.FindControl<TextBlock>("EmptyGuidanceText")!.Text = report.Guidance ??
                "Choose a PS3 package or folder containing EBOOT.BIN, .self, or .sprx files.";
        }
        this.FindControl<StackPanel>("PatchPanel")!.IsVisible = _canPatchPackage;
        int incompleteCount = report.Items.Count - report.FullyAnalyzedCount;
        if (_canPatchPackage && incompleteCount > 0)
        {
            this.FindControl<Border>("CoverageWarningPanel")!.IsVisible = true;
            this.FindControl<TextBlock>("CoverageWarningText")!.Text =
                $"Only {report.FullyAnalyzedCount} of {report.Items.Count} executable(s) were fully analyzed. " +
                $"The {report.PatchableCount} verified executable(s) can be patched, but {incompleteCount} " +
                $"header-only executable(s) will remain unchanged. Compatibility below " +
                $"{report.HighestRequiredFirmware ?? "the detected requirement"} is not guaranteed.";
        }
        if (report.MissingRapCount > 0)
        {
            this.FindControl<Border>("RapHintPanel")!.IsVisible = true;
            this.FindControl<TextBlock>("RapHintText")!.Text =
                $"{report.MissingRapCount} licensed executable(s) are header-only because their RAP is missing. " +
                "PkgLens already searched beside the input and your Keys-page search folders. Add the matching " +
                "<content-id>.rap to the RAP library, then run this analysis again.";
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnPatch(object? sender, RoutedEventArgs e)
    {
        if (!_canPatchPackage)
            return;
        string[] parts = (_targetBox.Text ?? string.Empty).Trim().Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor) ||
            major is < 0 or > 15 || minor is < 0 or > 99)
        {
            Fail("Enter a firmware such as 4.00.");
            return;
        }
        int target = major * 100 + minor;
        if (_highestPatchableVersion > 0 && target >= _highestPatchableVersion)
        {
            Fail($"Choose a target below {FirmwareAnalysisReport.FormatVersion(_highestPatchableVersion)}.");
            return;
        }
        Close(new FirmwarePatchRequest(new CfwFirmwareTarget(major, minor)));
    }

    private void OnFirmwareRowSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        Border panel = this.FindControl<Border>("SelectedDetailsPanel")!;
        if (sender is not DataGrid { SelectedItem: FirmwareAnalysisRow row })
        {
            panel.IsVisible = false;
            return;
        }

        this.FindControl<TextBlock>("SelectedDetailsText")!.Text = $"{row.Path}: {row.Status}";
        panel.IsVisible = true;
    }

    private static string SuggestTarget(int requiredVersion)
    {
        if (requiredVersion <= 100)
            return string.Empty;

        int suggested = requiredVersion - 10;
        return $"{suggested / 100}.{suggested % 100:00}";
    }

    private async void OnSaveReport(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save firmware analysis report",
            SuggestedFileName = "firmware-analysis.txt",
            DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            await AtomicOutput.WriteAllTextAsync(path, FirmwareAnalyzer.ToText(_report));
        }
        catch (Exception ex)
        {
            await new ErrorDialog(new ViewModels.GuiErrorReport(
                "Could not save firmware report", ex.Message, ex.ToString())).ShowDialog(this);
        }
    }

    private void Fail(string message)
    {
        _patchError.Text = message;
        _patchError.IsVisible = true;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}

public sealed record FirmwareAnalysisRow(
    string Path, string Kind, string HeaderFirmware, string SdkFirmware, string Analysis, string Status)
{
    public static FirmwareAnalysisRow From(FirmwareAnalysisItem item) => new(
        item.Path, item.Kind, item.HeaderFirmware ?? "—", item.SdkFirmware ?? "—",
        item.State switch
        {
            FirmwareAnalysisState.Patchable => "Ready to patch",
            FirmwareAnalysisState.NoPatchMarker => "Fully analyzed",
            FirmwareAnalysisState.MissingRap => "RAP needed",
            FirmwareAnalysisState.DecryptionUnavailable => "Header only",
            _ => "Could not analyze",
        },
        item.Status);
}
