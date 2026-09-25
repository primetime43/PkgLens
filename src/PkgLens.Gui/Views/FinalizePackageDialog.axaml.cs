using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Shared;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class FinalizePackageDialog : Window
{
    private string? _source, _output;
    private bool _eligible, _closeAfterCancel;
    private CancellationTokenSource? _cancellation;

    public FinalizePackageDialog() : this(null) { }
    public FinalizePackageDialog(string? sourcePath)
    {
        AvaloniaXamlLoader.Load(this);
        if (sourcePath is not null) Opened += async (_, _) => await InspectAsync(sourcePath);
    }
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Choose a non-finalized PS3 package", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } } },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await InspectAsync(path);
    }

    internal async Task InspectAsync(string path)
    {
        if (_cancellation is not null) return;
        _source = path; _output = null; _eligible = false;
        C<TextBlock>("SourceText").Text = path;
        ToolTip.SetTip(C<TextBlock>("SourceText"), path);
        C<TextBlock>("OutputText").IsVisible = false;
        C<TextBlock>("InfoText").Text = "Checking package…";
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        UpdateButtons();
        C<ProgressBar>("Progress").IsIndeterminate = true;
        C<TextBlock>("StatusText").Text = "Checking format, package structure and source checksum…";
        try
        {
            var info = await Task.Run(() =>
            {
                using var source = File.OpenRead(path);
                return PkgFinalizer.Inspect(source, cancellation.Token);
            });
            C<TextBlock>("InfoText").Text = $"Non-finalized PS3 package\n{info.ContentId.Raw}\n"
                + $"{info.FileCount:n0} files · {info.DirectoryCount:n0} folders · {info.Header.TotalSize:n0} bytes";
            _eligible = true;
            C<TextBlock>("StatusText").Text = "Ready. Choose where to save the finalized copy.";
        }
        catch (OperationCanceledException) { C<TextBlock>("InfoText").Text = "Check cancelled."; C<TextBlock>("StatusText").Text = "No output was written."; }
        catch (Exception ex) { C<TextBlock>("InfoText").Text = "This package cannot be finalized."; C<TextBlock>("StatusText").Text = ex.Message; }
        finally { FinishWork(); }
    }

    private async void OnFinalize(object? sender, RoutedEventArgs e)
    {
        if (!_eligible || _source is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Save finalized PS3 package", SuggestedFileName = Path.GetFileNameWithoutExtension(_source) + "-finalized.pkg",
            DefaultExtension = "pkg", FileTypeChoices = new[] { new FilePickerFileType("PS3 package") { Patterns = new[] { "*.pkg" } } },
        });
        if (file?.TryGetLocalPath() is { } path) await FinalizeAsync(path);
    }

    internal async Task FinalizeAsync(string destination)
    {
        if (!_eligible || _source is null || _cancellation is not null) return;
        _output = null; C<TextBlock>("OutputText").IsVisible = false;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        UpdateButtons();
        C<TextBlock>("StatusText").Text = "Checking source and finalizing…";
        C<ProgressBar>("Progress").IsIndeterminate = true;
        var progress = new Progress<PkgOperationProgress>(value =>
        {
            if (_cancellation != cancellation) return;
            C<ProgressBar>("Progress").IsIndeterminate = false;
            C<ProgressBar>("Progress").Value = value.Percent;
            C<TextBlock>("StatusText").Text = $"{value.Item}… {value.Percent:0}%";
        });
        try
        {
            string source = _source;
            var report = await Task.Run(() => PackageFinalizationService.FinalizeFile(source, destination, cancellation.Token, progress));
            _output = destination;
            C<TextBlock>("OutputText").Text = "Saved: " + destination;
            C<TextBlock>("OutputText").IsVisible = true;
            C<TextBlock>("StatusText").Text = $"Finalized and verified {report.FileCount:n0} files. Contents match the source exactly.";
        }
        catch (OperationCanceledException) { C<TextBlock>("StatusText").Text = "Cancelled. No finalized copy was published."; }
        catch (Exception ex) { C<TextBlock>("StatusText").Text = "Finalization failed: " + ex.Message; }
        finally { FinishWork(); }
    }

    private void FinishWork()
    {
        _cancellation = null; UpdateButtons();
        if (_closeAfterCancel) Close();
    }
    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        C<Button>("BrowseButton").IsEnabled = !busy;
        C<Button>("FinalizeButton").IsEnabled = !busy && _eligible;
        C<Button>("StopButton").IsVisible = busy;
        C<ProgressBar>("Progress").IsVisible = busy;
        C<Button>("OpenOutputButton").IsVisible = !busy && _output is not null;
    }
    private void OnStop(object? sender, RoutedEventArgs e)
    { _cancellation?.Cancel(); C<TextBlock>("StatusText").Text = "Cancelling…"; }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (_output is null) return;
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(Path.GetFullPath(_output))!) { UseShellExecute = true }); }
        catch (Exception ex) { C<TextBlock>("StatusText").Text = ex.Message; }
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_cancellation is not null) { e.Cancel = true; _closeAfterCancel = true; _cancellation.Cancel(); }
        base.OnClosing(e);
    }
}
