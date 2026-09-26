using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class VerifySelfDialog : Window
{
    private readonly string? _rapDirectory;
    private string? _source;
    private CancellationTokenSource? _cancellation;
    private bool _ready, _closeAfterCancel;

    public VerifySelfDialog() : this(null) { }
    public VerifySelfDialog(string? rapDirectory)
    {
        _rapDirectory = rapDirectory;
        AvaloniaXamlLoader.Load(this);
        C<TextBlock>("ScopeText").Text = SelfVerificationReport.Scope;
        _ready = true;
    }
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Verify an existing SELF / EBOOT", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("SELF / EBOOT / SPRX")
                { Patterns = new[] { "EBOOT.BIN", "*.self", "*.sprx", "*.bin" } }, FilePickerFileTypes.All },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await VerifyAsync(path);
    }

    private async void OnChooseRap(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Choose the matching RAP", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("RAP license") { Patterns = new[] { "*.rap" } } },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) C<TextBox>("RapBox").Text = path;
    }
    private void OnClearRap(object? sender, RoutedEventArgs e) => C<TextBox>("RapBox").Text = "";
    private void OnKeyChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready || _cancellation is not null) return;
        C<StackPanel>("ResultsPanel").IsVisible = false;
        C<TextBlock>("StatusText").Text = _source is null ? "Choose a file to check." : "Key selection changed. Verify again to update the results.";
    }
    private async void OnVerify(object? sender, RoutedEventArgs e)
    { if (_source is { } path) await VerifyAsync(path); }

    internal async Task VerifyAsync(string path)
    {
        if (_cancellation is not null) return;
        _source = path;
        C<TextBlock>("SourceText").Text = path;
        ToolTip.SetTip(C<TextBlock>("SourceText"), path);
        C<StackPanel>("ResultsPanel").IsVisible = false;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        var key = new EdatKeySelection(C<TextBox>("KeyBox").Text, C<TextBox>("RapBox").Text);
        UpdateButtons();
        C<TextBlock>("StatusText").Text = "Checking header signature and recovering ELF in memory…";
        try
        {
            var report = await Task.Run(() => SelfVerificationService.VerifyFile(path, key, _rapDirectory, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            C<TextBlock>("SignatureText").Text = "Header signature: " + report.Signature.Status;
            C<TextBlock>("SignatureDetail").Text = report.Signature.Detail;
            C<TextBlock>("DecryptionText").Text = "Decryption: " + report.Decryption.Status;
            C<TextBlock>("DecryptionDetail").Text = report.Decryption.Detail;
            C<TextBlock>("TargetText").Text = "CEX / DEX: " + report.Target.Label;
            C<TextBlock>("TargetDetail").Text = report.TargetDetail;
            var info = report.Info;
            C<TextBlock>("InfoText").Text = $"Program: {info.ProgramTypeText} · Key revision: 0x{info.KeyRevision:X4}\n"
                + $"Firmware field: {info.FirmwareVersionText ?? "Not set"} · File size: {info.FileSize:n0} bytes\n"
                + (info.Npdrm is { } np ? $"License: {np.LicenseText}\nContent ID: {np.ContentId}"
                    : info.IsNpdrm ? "License: Missing / unreadable NPDRM metadata" : "License: Not NPDRM");
            C<StackPanel>("ResultsPanel").IsVisible = true;
            C<TextBlock>("StatusText").Text = "Check complete. Review each result above; no files were changed.";
        }
        catch (OperationCanceledException) { C<TextBlock>("StatusText").Text = "Check cancelled. No files were changed."; }
        catch (Exception ex) { C<TextBlock>("StatusText").Text = "Could not verify this file: " + ex.Message; }
        finally
        {
            _cancellation = null; UpdateButtons();
            if (_closeAfterCancel) Close();
        }
    }
    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        C<Button>("BrowseButton").IsEnabled = !busy;
        C<Expander>("KeysPanel").IsEnabled = !busy;
        C<Button>("VerifyButton").IsEnabled = !busy && _source is not null;
        C<Button>("StopButton").IsVisible = busy;
        C<ProgressBar>("Progress").IsVisible = busy;
    }
    private void OnStop(object? sender, RoutedEventArgs e)
    { _cancellation?.Cancel(); C<TextBlock>("StatusText").Text = "Cancelling…"; }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_cancellation is not null) { e.Cancel = true; _closeAfterCancel = true; _cancellation.Cancel(); }
        base.OnClosing(e);
    }
}
