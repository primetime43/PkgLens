using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class SelfFolderDialog : Window
{
    private SelfFolderPlan? _plan;
    private CancellationTokenSource? _cancellation;
    private readonly ObservableCollection<SelfFolderJob> _rows = new();
    private bool _ready;
    private bool _closeAfterCancel;

    public SelfFolderDialog() : this(null) { }
    public SelfFolderDialog(string? rapDirectory)
    {
        AvaloniaXamlLoader.Load(this);
        C<ComboBox>("OperationBox").ItemsSource = new[]
        { "Decrypt to ELF", "Rebuild encrypted SELF", "Build fake-signed SELF", "Legacy CEX signing" };
        C<ComboBox>("OperationBox").SelectedIndex = 1;
        C<TextBox>("RapBox").Text = rapDirectory;
        C<DataGrid>("JobsGrid").ItemsSource = _rows;
        foreach (string name in new[] { "CompressCheck", "RecursiveCheck" })
            C<CheckBox>(name).PropertyChanged += (_, e) =>
            { if (_ready && e.Property == ToggleButton.IsCheckedProperty) ClearPlan(); };
        _ready = true;
        UpdateOperation();
    }

    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    private SelfFolderOperation Operation => (SelfFolderOperation)C<ComboBox>("OperationBox").SelectedIndex;

    private async Task<string?> ChooseFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnChooseSource(object? sender, RoutedEventArgs e)
    {
        if (await ChooseFolder("Choose a folder containing PS3 executables") is not { } path) return;
        C<TextBox>("SourceBox").Text = path;
        if (string.IsNullOrWhiteSpace(C<TextBox>("OutputBox").Text))
            C<TextBox>("OutputBox").Text = Path.TrimEndingDirectorySeparator(path) + " - SELF output";
    }
    private async void OnChooseOutput(object? sender, RoutedEventArgs e)
    { if (await ChooseFolder("Choose a separate output folder") is { } path) C<TextBox>("OutputBox").Text = path; }
    private async void OnChooseRaps(object? sender, RoutedEventArgs e)
    { if (await ChooseFolder("Choose the RAP library") is { } path) C<TextBox>("RapBox").Text = path; }

    private void OnSettingsChanged(object? sender, TextChangedEventArgs e) { if (_ready) ClearPlan(); }
    private void OnOperationChanged(object? sender, SelectionChangedEventArgs e)
    { if (_ready) { ClearPlan(); UpdateOperation(); } }

    private void UpdateOperation()
    {
        C<Control>("RevisionPanel").IsVisible = Operation is SelfFolderOperation.Rebuild or SelfFolderOperation.LegacySign;
        C<Control>("CompressCheck").IsVisible = Operation != SelfFolderOperation.Decrypt;
        C<TextBlock>("OperationDescription").Text = Operation switch
        {
            SelfFolderOperation.Decrypt => "Recover and validate each ELF. Output names end in .elf; existing plaintext ELFs are skipped.",
            SelfFolderOperation.Rebuild => "Preserve APP/NPDRM type, content ID and license. Encrypt at the selected revision and verify an exact ELF round-trip. No header signature is added.",
            SelfFolderOperation.FakeSign => "Preserve APP/NPDRM metadata and verify an exact ELF round-trip. Produces debug/fake-signed output for compatible CFW.",
            _ => "Preserve APP/NPDRM metadata, encrypt and verify the legacy header signature and ELF round-trip. Legacy keys: 01, 04, 07, 0A; 00 is APP only. This does not establish retail OFW compatibility.",
        };
    }

    private void ClearPlan()
    {
        _plan = null;
        _rows.Clear();
        C<TextBox>("DetailText").Text = "";
        C<TextBlock>("StatusText").Text = "Settings changed. Scan the folder to preview outputs.";
        UpdateButtons();
    }

    private async void OnScan(object? sender, RoutedEventArgs e) => await ScanAsync();
    internal async Task ScanAsync()
    {
        if (_cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _plan = null; _rows.Clear();
        UpdateButtons();
        C<TextBlock>("StatusText").Text = "Scanning executables…";
        C<ProgressBar>("Progress").IsIndeterminate = true;
        try
        {
            string source = C<TextBox>("SourceBox").Text?.Trim() ?? "";
            string output = C<TextBox>("OutputBox").Text?.Trim() ?? "";
            if (source.Length == 0 || output.Length == 0) throw new IOException("Choose both source and output folders.");
            ushort revision = 0x0A;
            if (Operation is SelfFolderOperation.Rebuild or SelfFolderOperation.LegacySign
                && !ushort.TryParse(C<TextBox>("RevisionBox").Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out revision))
                throw new IOException("Enter a hexadecimal key revision, such as 0A.");
            var options = new SelfFolderOptions(Operation, revision, C<CheckBox>("CompressCheck").IsChecked == true,
                C<CheckBox>("RecursiveCheck").IsChecked == true);
            _plan = await Task.Run(() => SelfFolderService.Scan(source, output, options, cancellation.Token));
            foreach (var job in _plan.Jobs) _rows.Add(job with { });
            ShowSummary();
        }
        catch (OperationCanceledException) { C<TextBlock>("StatusText").Text = "Scan cancelled."; }
        catch (Exception ex) { C<TextBlock>("StatusText").Text = ex.Message; }
        finally { FinishWork(); }
    }

    private async void OnRun(object? sender, RoutedEventArgs e) => await RunAsync(false);
    private async void OnRetry(object? sender, RoutedEventArgs e) => await RunAsync(true);
    internal async Task RunAsync(bool retryFailed)
    {
        if (_plan is null || _cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        UpdateButtons();
        C<Expander>("KeysPanel").IsExpanded = false;
        C<ProgressBar>("Progress").IsIndeterminate = false;
        C<ProgressBar>("Progress").Value = 0;
        var activePlan = _plan;
        var progress = new Progress<SelfFolderProgress>(p =>
        {
            if (_plan != activePlan || _cancellation != cancellation) return;
            _rows[p.Index] = p.Job;
            C<ProgressBar>("Progress").Value = p.Total == 0 ? 100 : p.Finished * 100d / p.Total;
            if (_cancellation is not null)
                C<TextBlock>("StatusText").Text = $"{p.Finished}/{p.Total} processed · {p.Job.RelativePath}";
        });
        try
        {
            var plan = _plan;
            var key = new EdatKeySelection(C<TextBox>("KeyBox").Text);
            string? raps = C<TextBox>("RapBox").Text;
            // Reject an invalid global override once, rather than failing every job.
            EdatWorkbenchService.ParseKey(key.RawKey);
            await Task.Run(() => SelfFolderService.Run(plan, key, raps, retryFailed, cancellation.Token, progress));
            // Use the final worker state even if a queued progress notification has not arrived yet.
            for (int i = 0; i < plan.Jobs.Count; i++) _rows[i] = plan.Jobs[i] with { };
            ShowSummary(cancellation.IsCancellationRequested ? "Stopped. " : "");
        }
        catch (Exception ex) { C<TextBlock>("StatusText").Text = ex.Message; }
        finally { FinishWork(); }
    }

    private void ShowSummary(string prefix = "")
    {
        if (_plan is null) return;
        var jobs = _plan.Jobs;
        C<TextBlock>("StatusText").Text = jobs.Count == 0 ? "No SELF, SPRX, ELF or EBOOT.BIN files found."
            : prefix + $"{jobs.Count(j => j.Status == SelfFolderStatus.Complete)} complete · "
                + $"{jobs.Count(j => j.Status is SelfFolderStatus.Pending or SelfFolderStatus.Cancelled)} ready · "
                + $"{jobs.Count(j => j.Status == SelfFolderStatus.Failed)} failed · "
                + $"{jobs.Count(j => j.Status == SelfFolderStatus.Skipped)} skipped";
    }

    private void FinishWork()
    {
        _cancellation = null;
        UpdateButtons();
        if (_closeAfterCancel) Close();
    }
    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        C<Control>("SetupPanel").IsEnabled = !busy;
        C<Control>("KeysPanel").IsEnabled = !busy;
        C<Button>("ScanButton").IsEnabled = !busy;
        C<Button>("RunButton").IsEnabled = !busy && _plan?.Jobs.Any(j => j.Status is SelfFolderStatus.Pending or SelfFolderStatus.Cancelled) == true;
        C<Button>("RetryButton").IsEnabled = !busy && _plan?.Jobs.Any(j => j.Status == SelfFolderStatus.Failed) == true;
        C<Button>("CancelButton").IsVisible = busy;
        C<ProgressBar>("Progress").IsVisible = busy;
        C<Button>("OpenOutputButton").IsEnabled = !busy && _plan is not null && Directory.Exists(_plan.OutputRoot);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (C<DataGrid>("JobsGrid").SelectedItem is SelfFolderJob job)
            C<TextBox>("DetailText").Text = $"{job.RelativePath} · {job.Status} · Attempts: {job.Attempts}\nSource CEX / DEX: {job.Target.Label}. {job.Target.Detail}\n{job.Message}\n{job.Output}";
    }
    private void OnCancel(object? sender, RoutedEventArgs e)
    { _cancellation?.Cancel(); C<TextBlock>("StatusText").Text = "Cancelling; waiting for the current operation to stop…"; }
    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        try { Process.Start(new ProcessStartInfo(_plan.OutputRoot) { UseShellExecute = true }); }
        catch (Exception ex) { C<TextBlock>("StatusText").Text = ex.Message; }
    }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_cancellation is not null)
        {
            e.Cancel = true;
            _closeAfterCancel = true;
            _cancellation.Cancel();
            C<TextBlock>("StatusText").Text = "Cancelling before closing…";
        }
        base.OnClosing(e);
    }
}
