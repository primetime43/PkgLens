using System;
using System.Collections.ObjectModel;
using System.Globalization;
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

internal sealed record PackageSelfSelection(PreparedPackageExecutables? Prepared);

public partial class PackageSelfDialog : Window
{
    private readonly Func<PackageSelfSettings, CancellationToken, IProgress<PackageSelfCheck>, PreparedPackageExecutables>? _prepare;
    private readonly ObservableCollection<PackageSelfCheck> _rows = new();
    private PreparedPackageExecutables? _prepared;
    private CancellationTokenSource? _cancellation;
    private bool _ready, _closeAfterCancel;
    public PackageSelfDialog() : this(null, 0, null) { }
    internal PackageSelfDialog(string? rapDirectory, int initialMode,
        Func<PackageSelfSettings, CancellationToken, IProgress<PackageSelfCheck>, PreparedPackageExecutables>? prepare)
    {
        _prepare = prepare;
        AvaloniaXamlLoader.Load(this);
        C<ComboBox>("ModeBox").ItemsSource = new[] { "Keep executables unchanged", "Rebuild encrypted SELF", "Build fake-signed SELF", "Legacy CEX signing" };
        C<ComboBox>("ModeBox").SelectedIndex = initialMode;
        C<TextBox>("RapBox").Text = rapDirectory;
        C<DataGrid>("ChecksGrid").ItemsSource = _rows;
        C<CheckBox>("CompressCheck").PropertyChanged += (_, e) =>
        { if (_ready && e.Property == ToggleButton.IsCheckedProperty) Invalidate(); };
        _ready = true;
        UpdateMode();
    }
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    private int Mode => C<ComboBox>("ModeBox").SelectedIndex;
    private void OnSettingsChanged(object? sender, TextChangedEventArgs e) { if (_ready && _cancellation is null) Invalidate(); }
    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    { if (_ready) { Invalidate(); UpdateMode(); } }
    private void Invalidate()
    {
        _prepared?.Dispose(); _prepared = null; _rows.Clear();
        C<TextBox>("DetailText").Text = "";
        C<TextBlock>("StatusText").Text = Mode == 0 ? "Pending file replacements and SFO edits will be saved as usual."
            : "Check executables before saving. Every executable must pass; failures are never silently skipped.";
        UpdateButtons();
    }
    private void UpdateMode()
    {
        C<Control>("BuildSettings").IsVisible = Mode != 0;
        C<Control>("RevisionPanel").IsVisible = Mode is 1 or 3;
        C<Button>("CheckButton").IsVisible = Mode != 0;
        C<DataGrid>("ChecksGrid").IsVisible = Mode != 0;
        C<TextBox>("DetailText").IsVisible = Mode != 0;
        MinHeight = Mode == 0 ? 240 : 600;
        Height = Mode == 0 ? 260 : Math.Max(680, Height);
        C<TextBlock>("ProfileHint").Text = Mode == 3
            ? "Legacy profiles: 01, 04, 07, 0A (APP and NPDRM); 00 (APP only). Verifies header signatures; does not sign the PKG or establish stock-console compatibility."
            : Mode == 1 ? "Verifies encryption with an exact ELF round-trip. Header signatures remain unsigned."
            : "Debug/fake-signed output for compatible CFW; verifies an exact ELF round-trip.";
        Invalidate();
    }
    private async void OnPickRaps(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose RAP library", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) C<TextBox>("RapBox").Text = path;
    }
    private async void OnCheck(object? sender, RoutedEventArgs e) => await CheckAsync();
    internal async Task CheckAsync()
    {
        if (_cancellation is not null || Mode == 0 || _prepare is null) return;
        Invalidate();
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        UpdateButtons();
        var progress = new Progress<PackageSelfCheck>(check =>
        {
            if (_cancellation != cancellation) return;
            var existing = _rows.FirstOrDefault(r => r.Name == check.Name);
            if (existing is null) _rows.Add(check); else _rows[_rows.IndexOf(existing)] = check;
            C<TextBlock>("StatusText").Text = $"{check.Status}: {check.Name}";
        });
        try
        {
            ushort revision = 0x0A;
            if (Mode is 1 or 3 && !ushort.TryParse(C<TextBox>("RevisionBox").Text, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out revision)) throw new ArgumentException("Enter a hex revision such as 0A.");
            var operation = Mode switch { 1 => SelfFolderOperation.Rebuild, 2 => SelfFolderOperation.FakeSign, _ => SelfFolderOperation.LegacySign };
            var settings = new PackageSelfSettings(operation, revision, C<CheckBox>("CompressCheck").IsChecked == true,
                C<TextBox>("RapBox").Text, C<TextBox>("KeyBox").Text);
            _prepared = await Task.Run(() => _prepare(settings, cancellation.Token, progress));
            _rows.Clear(); foreach (var row in _prepared.Checks) _rows.Add(row);
            C<TextBlock>("StatusText").Text = _prepared.CanBuild
                ? $"{_rows.Count} executable(s) prepared and verified. Ready to save the package."
                : $"{_rows.Count(r => r.Status == "Blocked")} executable(s) blocked. Fix the listed problems and check again.";
        }
        catch (OperationCanceledException) { C<TextBlock>("StatusText").Text = "Check cancelled. No package was written."; }
        catch (Exception ex) { C<TextBlock>("StatusText").Text = ex.Message; }
        finally
        {
            _cancellation = null; UpdateButtons();
            if (_closeAfterCancel) Close();
        }
    }
    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        C<Control>("SettingsPanel").IsEnabled = !busy;
        C<Button>("CheckButton").IsEnabled = !busy;
        C<Button>("ContinueButton").IsEnabled = !busy && (Mode == 0 || _prepared?.CanBuild == true);
        C<Button>("CancelWorkButton").IsVisible = busy;
        C<ProgressBar>("Progress").IsVisible = busy;
    }
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (C<DataGrid>("ChecksGrid").SelectedItem is PackageSelfCheck check)
            C<TextBox>("DetailText").Text = check.Name + "\n" + check.Message;
    }
    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        if (_cancellation is not null || Mode != 0 && _prepared?.CanBuild != true) return;
        var result = new PackageSelfSelection(_prepared);
        _prepared = null; // Ownership passes to the package build command.
        Close(result);
    }
    private void OnStop(object? sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_cancellation is not null) { e.Cancel = true; _closeAfterCancel = true; _cancellation.Cancel(); }
        else { _prepared?.Dispose(); _prepared = null; }
        base.OnClosing(e);
    }
}
