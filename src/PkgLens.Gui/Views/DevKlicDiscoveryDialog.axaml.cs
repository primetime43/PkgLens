using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class DevKlicDiscoveryDialog : Window
{
    private readonly string? _rapDirectory, _databasePath;
    private string? _target, _executable, _rap;
    private DevKlicMatch? _match;
    private CancellationTokenSource? _cancellation;
    private bool _ready;
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    public DevKlicDiscoveryDialog() : this(null) { }
    public DevKlicDiscoveryDialog(string? rapDirectory, string? databasePath = null)
    {
        AvaloniaXamlLoader.Load(this);
        _rapDirectory = rapDirectory; _databasePath = databasePath; _ready = true;
    }

    internal void SetTarget(string path, bool locked = false)
    {
        _target = path; C<TextBlock>("TargetName").Text = Path.GetFileName(path);
        C<Button>("ChooseTarget").IsEnabled = !locked; Invalidate();
    }
    internal void SetExecutable(string path)
    {
        _executable = path; _rap = null;
        C<TextBox>("ExecutableKey").Text = "";
        C<TextBlock>("RapName").Text = "Automatic lookup";
        C<TextBlock>("ExecutableName").Text = Path.GetFileName(path); Invalidate();
    }
    private void Invalidate()
    {
        _match?.Dispose(); _match = null;
        C<Button>("SaveKeyButton").IsEnabled = false;
        C<TextBlock>("ResultText").Text = "Search to find a confirmed key for the current files and settings.";
        C<TextBlock>("Status").Text = "No key has been saved.";
    }
    private void OnSettingsChanged(object? sender, RoutedEventArgs e) { if (_ready) Invalidate(); }
    private async Task<string?> Pick(string title, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Supported files") { Patterns = patterns }, FilePickerFileTypes.All] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private async void OnChooseTarget(object? sender, RoutedEventArgs e)
    { if (await Pick("Choose the EDAT to test", ["*.edat"]) is { } path) SetTarget(path); }
    private async void OnChooseExecutable(object? sender, RoutedEventArgs e)
    { if (await Pick("Choose PS3 executable", ["*.bin", "*.self", "*.sprx", "*.elf"]) is { } path) SetExecutable(path); }
    private async void OnChooseRap(object? sender, RoutedEventArgs e)
    { if (await Pick("Choose the executable's RAP", ["*.rap"]) is { } path) { _rap = path; C<TextBlock>("RapName").Text = Path.GetFileName(path); Invalidate(); } }
    private void OnClearRap(object? sender, RoutedEventArgs e)
    { _rap = null; C<TextBlock>("RapName").Text = "Automatic lookup"; Invalidate(); }
    private async void OnSearch(object? sender, RoutedEventArgs e) => await SearchAsync();
    internal async Task SearchAsync()
    {
        if (_cancellation is not null) return;
        Invalidate();
        if (_target is null || _executable is null)
        { C<TextBlock>("Status").Text = "Choose both an EDAT and an executable."; return; }
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; SetBusy(true);
        var target = _target; var executable = _executable;
        var selection = new EdatKeySelection(C<TextBox>("ExecutableKey").Text, _rap);
        var progress = new Progress<DevKlicProgress>(p =>
        {
            if (_cancellation != cancellation) return;
            C<ProgressBar>("SearchProgress").Value = p.Percent;
            C<TextBlock>("Status").Text = $"{p.Phase} · {p.CandidatesTested:n0} candidates tested";
        });
        try
        {
            _match = await Task.Run(() => DevKlicDiscoveryService.Discover(executable, target, selection,
                _rapDirectory, _databasePath, cancellation.Token, progress), cancellation.Token);
            C<TextBlock>("ResultText").Text = _match?.Description ??
                "No confirmed key was found. Try another executable from this game. The key may be assembled at runtime, stored elsewhere, or the target EDAT may be damaged.";
            C<TextBlock>("Status").Text = _match is null ? "Search complete. No key was saved." :
                "Match verified. Save it to enable automatic lookup for this EDAT filename, content ID, and license.";
        }
        catch (OperationCanceledException) { C<TextBlock>("ResultText").Text = "Search cancelled. No key was saved."; C<TextBlock>("Status").Text = "Cancelled."; }
        catch (Exception ex) { C<TextBlock>("ResultText").Text = ex.Message; C<TextBlock>("Status").Text = "Search failed. No key was saved."; }
        finally { _cancellation = null; SetBusy(false); }
    }

    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveAsync();
    internal async Task SaveAsync()
    {
        if (_match is null || _cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; SetBusy(true);
        var match = _match;
        C<TextBlock>("Status").Text = "Rechecking the EDAT and saving its confirmed key…";
        bool saved = false;
        try { await Task.Run(() => match.Save(_databasePath, cancellation.Token)); saved = true; }
        catch (OperationCanceledException) { C<TextBlock>("Status").Text = "Save cancelled."; }
        catch (Exception ex) { C<TextBlock>("Status").Text = "Key was not saved. " + ex.Message; }
        finally { _cancellation = null; SetBusy(false); }
        if (saved) Close(true);
    }
    private void SetBusy(bool busy)
    {
        C<Control>("Inputs").IsEnabled = !busy;
        C<Button>("SearchButton").IsEnabled = !busy;
        C<Button>("SaveKeyButton").IsEnabled = !busy && _match is not null;
        C<Button>("CloseButton").IsEnabled = !busy;
        C<Button>("CancelButton").IsVisible = busy;
        C<ProgressBar>("SearchProgress").IsVisible = busy;
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (_cancellation is not null) { e.Cancel = true; _cancellation.Cancel(); } base.OnClosing(e); }
    protected override void OnClosed(EventArgs e) { _match?.Dispose(); base.OnClosed(e); }
}
