using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class EdatWorkbenchDialog : Window
{
    private bool _ready;
    private string? _source, _replacement, _inputRap, _outputRap, _packageName, _packagePath, _packageScratch;
    private readonly string? _rapDirectory;
    private CancellationTokenSource? _cancellation;
    private EdatWorkbenchResult? _result;
    private EdatWorkbenchRequest? _builtRequest;
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    private EdatWorkbenchOperation Mode => (EdatWorkbenchOperation)C<ComboBox>("Operation").SelectedIndex;

    public EdatWorkbenchDialog() : this(null) { }
    public EdatWorkbenchDialog(string? rapDirectory)
    {
        AvaloniaXamlLoader.Load(this);
        _rapDirectory = rapDirectory;
        _ready = true;
        RefreshMode();
    }

    public void LoadPackageEntry(string fileName, byte[] data, string packagePath)
    {
        _packageName = Path.GetFileName(fileName);
        _packagePath = packagePath;
        _packageScratch = Path.Combine(Path.GetTempPath(), "pkglens-edat-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_packageScratch);
        string path = Path.Combine(_packageScratch, _packageName);
        File.WriteAllBytes(path, data);
        C<ComboBox>("Operation").SelectedIndex = (int)EdatWorkbenchOperation.QuickRebuild;
        SetSource(path);
        C<Button>("ChooseSource").IsEnabled = false;
        C<Button>("StageButton").IsVisible = true;
    }

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Invalidate();
        RefreshMode();
        if (_source is not null) C<TextBox>("OutputName").Text = DefaultName();
    }

    private void RefreshMode()
    {
        bool rebuild = Mode is EdatWorkbenchOperation.QuickRebuild or EdatWorkbenchOperation.CustomRebuild;
        C<Control>("ReplacementPanel").IsVisible = rebuild;
        C<Control>("InputKeyPanel").IsVisible = Mode != EdatWorkbenchOperation.Encrypt;
        C<Control>("OutputSettings").IsVisible = Mode is EdatWorkbenchOperation.Encrypt or EdatWorkbenchOperation.CustomRebuild;
        C<TextBlock>("ModeHelp").Text = Mode switch
        {
            EdatWorkbenchOperation.Decrypt => "Decrypt an EDAT or SDAT to plaintext using its key or license.",
            EdatWorkbenchOperation.Encrypt => "Protect a plaintext file as EDAT or SDAT with the output settings below.",
            EdatWorkbenchOperation.QuickRebuild => "Keep the original filename, NPD identity, license, version, and block size. Optionally replace the plaintext. Compressed input is rebuilt uncompressed.",
            _ => "Decrypt the original, optionally replace its plaintext, then rebuild using new output settings.",
        };
        C<Button>("BuildButton").Content = Mode == EdatWorkbenchOperation.Decrypt ? "Decrypt and verify" : "Build and verify";
        RefreshKeyFields();
    }

    private void OnFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (C<ComboBox>("Format").SelectedIndex == 1 && C<ComboBox>("Version").SelectedIndex == 0)
            C<ComboBox>("Version").SelectedIndex = 2;
        C<ComboBox>("License").IsEnabled = C<ComboBox>("Format").SelectedIndex == 0;
        if (_source is not null && Mode == EdatWorkbenchOperation.Encrypt) C<TextBox>("OutputName").Text = DefaultName();
        Invalidate();
        RefreshKeyFields();
    }

    private void OnSettingsChanged(object? sender, RoutedEventArgs e) { if (_ready) { Invalidate(); RefreshKeyFields(); } }

    private void RefreshKeyFields()
    {
        bool sdat = C<ComboBox>("Format").SelectedIndex == 1;
        bool licensed = C<ComboBox>("License").SelectedIndex != 2;
        C<Control>("OutputRawKey").IsVisible = !sdat;
        C<Control>("OutputRapPanel").IsVisible = !sdat && licensed;
        C<Control>("DeveloperSettings").IsVisible = !sdat && licensed;
    }

    private void Invalidate()
    {
        _result?.Dispose();
        _result = null;
        _builtRequest = null;
        C<Button>("PreviewButton").IsEnabled = false;
        C<Button>("SaveButton").IsEnabled = false;
        C<Button>("StageButton").IsEnabled = false;
        C<TextBlock>("KeyCheckStatus").Text = "Key not checked for the current settings. Check key and file before processing.";
        C<TextBlock>("Status").Text = "Build or decrypt to preview and save the current settings.";
    }

    private string DefaultName() => Mode switch
    {
        EdatWorkbenchOperation.Decrypt => Path.GetFileName(_source) + ".decrypted",
        EdatWorkbenchOperation.Encrypt => Path.GetFileName(_source) + (C<ComboBox>("Format").SelectedIndex == 1 ? ".sdat" : ".edat"),
        _ => Path.GetFileName(_source) ?? "file.edat",
    };

    private async Task<string?> Pick(string title, string[]? patterns = null)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = patterns is null ? new[] { FilePickerFileTypes.All } :
                new[] { new FilePickerFileType("Supported files") { Patterns = patterns }, FilePickerFileTypes.All },
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnChooseSource(object? sender, RoutedEventArgs e)
    {
        if (await Pick(Mode == EdatWorkbenchOperation.Encrypt ? "Choose plaintext to encrypt" : "Choose PS3 EDAT / SDAT",
            Mode == EdatWorkbenchOperation.Encrypt ? null : ["*.edat", "*.sdat"]) is { } path) SetSource(path);
    }

    internal void SetSource(string path)
    {
        Invalidate();
        _source = path;
        _replacement = null;
        _inputRap = null;
        _outputRap = null;
        C<TextBox>("InputRawKey").Text = "";
        C<TextBox>("OutputRawKey").Text = "";
        C<TextBox>("DeveloperKey").Text = "";
        C<TextBlock>("OutputRapName").Text = "Automatic key / license lookup";
        C<TextBlock>("InputRapName").Text = "Automatic key / license lookup";
        C<TextBlock>("ReplacementName").Text = "Use original content";
        C<TextBlock>("SourceName").Text = _packageName is null ? path : $"{_packageName} (current package contents)";
        C<TextBox>("OutputName").Text = DefaultName();
        C<TextBlock>("SourceInfo").Text = "";
        if (Mode == EdatWorkbenchOperation.Encrypt) return;
        try
        {
            using var input = File.OpenRead(path);
            var info = EdatFile.ParseHeader(input);
            C<TextBlock>("SourceInfo").Text = $"{(info.IsSdat ? "SDAT" : "EDAT")} v{info.Version} · {info.LicenseText} · {info.FileSize:n0} plaintext bytes" +
                (info.IsCompressed ? " · compressed input" : "");
            C<ComboBox>("Format").SelectedIndex = info.IsSdat ? 1 : 0;
            C<ComboBox>("Version").SelectedIndex = Math.Clamp(info.Version - 1, 0, 3);
            C<ComboBox>("License").SelectedIndex = Math.Clamp(info.License - 1, 0, 2);
            C<ComboBox>("BlockSize").SelectedIndex = Math.Clamp((int)Math.Log2(info.BlockSize / 1024d), 0, 5);
            C<TextBox>("ContentId").Text = info.ContentId;
            C<NumericUpDown>("AppType").Value = (uint)info.Type;
        }
        catch (Exception ex) { C<TextBlock>("SourceInfo").Text = ex.Message; }
    }

    private async void OnChooseReplacement(object? sender, RoutedEventArgs e)
    {
        if (await Pick("Choose replacement plaintext") is not { } path) return;
        _replacement = path;
        C<TextBlock>("ReplacementName").Text = Path.GetFileName(path);
        Invalidate();
    }
    private void OnClearReplacement(object? sender, RoutedEventArgs e) { _replacement = null; C<TextBlock>("ReplacementName").Text = "Use original content"; Invalidate(); }
    private async void OnInputRap(object? sender, RoutedEventArgs e)
    { if (await Pick("Choose input RAP", ["*.rap"]) is { } path) { _inputRap = path; C<TextBlock>("InputRapName").Text = Path.GetFileName(path); Invalidate(); } }
    private async void OnOutputRap(object? sender, RoutedEventArgs e)
    { if (await Pick("Choose output RAP", ["*.rap"]) is { } path) { _outputRap = path; C<TextBlock>("OutputRapName").Text = Path.GetFileName(path); Invalidate(); } }
    private void OnClearInputRap(object? sender, RoutedEventArgs e) { _inputRap = null; C<TextBlock>("InputRapName").Text = "Automatic key / license lookup"; Invalidate(); }
    private void OnClearOutputRap(object? sender, RoutedEventArgs e) { _outputRap = null; C<TextBlock>("OutputRapName").Text = "Automatic key / license lookup"; Invalidate(); }

    private async void OnCheckKey(object? sender, RoutedEventArgs e) => await CheckKeyAsync();
    internal async Task CheckKeyAsync()
    {
        if (_cancellation is not null || Mode == EdatWorkbenchOperation.Encrypt) return;
        Invalidate();
        if (_source is null) { C<TextBlock>("KeyCheckStatus").Text = "Choose an EDAT or SDAT source file first."; return; }
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var source = _source;
        var selection = new EdatKeySelection(C<TextBox>("InputRawKey").Text, _inputRap);
        SetBusy(true);
        C<TextBlock>("KeyCheckStatus").Text = "Checking the key, header, and file content…";
        C<TextBlock>("Status").Text = "Validating without saving plaintext. Large files may take longer; you can cancel.";
        try
        {
            var check = await Task.Run(() => EdatKeyValidationService.Check(source, selection,
                _rapDirectory, token: cancellation.Token), cancellation.Token);
            C<TextBlock>("KeyCheckStatus").Text = check.DisplayText;
            C<TextBlock>("Status").Text = check.Status == EdatKeyCheckStatus.Verified
                ? "Key check passed. Choose Decrypt or Build when ready."
                : "Key check did not pass. See the input key details above.";
        }
        catch (OperationCanceledException)
        {
            C<TextBlock>("KeyCheckStatus").Text = "Key check cancelled; no verified result is available.";
            C<TextBlock>("Status").Text = "Cancelled. No files were saved or staged.";
        }
        catch (Exception ex)
        {
            C<TextBlock>("KeyCheckStatus").Text = "Key check failed: " + ex.Message;
            C<TextBlock>("Status").Text = "Could not complete key validation.";
        }
        finally { _cancellation = null; SetBusy(false); }
    }

    private async void OnBuild(object? sender, RoutedEventArgs e) => await BuildAsync();
    internal async Task BuildAsync()
    {
        if (_cancellation is not null) return;
        Invalidate();
        if (_source is null) { C<TextBlock>("Status").Text = "Choose a source file first."; return; }
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);
        try
        {
            var options = new EdatWriteOptions
            {
                IsSdat = C<ComboBox>("Format").SelectedIndex == 1, Version = C<ComboBox>("Version").SelectedIndex + 1,
                License = C<ComboBox>("License").SelectedIndex + 1, BlockSize = 1024 << C<ComboBox>("BlockSize").SelectedIndex,
                ContentId = C<TextBox>("ContentId").Text?.Trim() ?? "", AppType = (uint)(C<NumericUpDown>("AppType").Value ?? 1),
                DeveloperKey = (Mode is EdatWorkbenchOperation.Encrypt or EdatWorkbenchOperation.CustomRebuild) &&
                    C<ComboBox>("Format").SelectedIndex == 0 && C<ComboBox>("License").SelectedIndex != 2
                    ? EdatWorkbenchService.ParseKey(C<TextBox>("DeveloperKey").Text) : null,
            };
            var request = new EdatWorkbenchRequest(Mode, _source, C<TextBox>("OutputName").Text?.Trim() ?? "", options,
                new(C<TextBox>("InputRawKey").Text, _inputRap), new(C<TextBox>("OutputRawKey").Text,
                    C<Control>("OutputRapPanel").IsVisible ? _outputRap : null),
                _replacement, _rapDirectory);
            C<TextBlock>("Status").Text = "Processing and verifying…";
            var progress = new Progress<double>(value => { C<ProgressBar>("WorkProgress").IsIndeterminate = false; C<ProgressBar>("WorkProgress").Value = value; });
            _result = await Task.Run(() => EdatWorkbenchService.Build(request, cancellation.Token, progress), cancellation.Token);
            _builtRequest = request;
            C<TextBlock>("Status").Text = _result.IsEncrypted ? "Rebuilt and verified against the plaintext. Ready to preview, save, or stage." : "Decrypted successfully. Ready to preview or save.";
        }
        catch (OperationCanceledException) { C<TextBlock>("Status").Text = "Cancelled. No output was saved or staged."; }
        catch (Exception ex) { C<TextBlock>("Status").Text = ex.Message; }
        finally { _cancellation = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        C<Control>("Inputs").IsEnabled = !busy;
        C<Button>("BuildButton").IsEnabled = !busy;
        C<Button>("CheckKeyButton").IsEnabled = !busy;
        C<Button>("CloseButton").IsEnabled = !busy;
        C<Button>("CancelButton").IsVisible = busy;
        C<ProgressBar>("WorkProgress").IsVisible = busy;
        C<ProgressBar>("WorkProgress").IsIndeterminate = true;
        C<Button>("PreviewButton").IsEnabled = !busy && _result is not null;
        C<Button>("SaveButton").IsEnabled = !busy && _result is not null;
        C<Button>("StageButton").IsEnabled = !busy && _result is { IsEncrypted: true } &&
            _result.FileName.Equals(_packageName, StringComparison.Ordinal) && Mode != EdatWorkbenchOperation.Encrypt;
    }

    private async void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        try
        {
            if (new FileInfo(_result.PlaintextPath).Length > PackageViewModel.MaxPreviewBytes)
                throw new IOException("Plaintext exceeds the 32 MiB preview limit. Use Decrypt only and Save copy to inspect it externally.");
            byte[] bytes = await File.ReadAllBytesAsync(_result.PlaintextPath);
            await new FileViewerDialog(_result.FileName + " · plaintext", bytes, true).ShowDialog(this);
        }
        catch (Exception ex) { C<TextBlock>("Status").Text = ex.Message; }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_result is null || _builtRequest is null || _cancellation is not null) return;
        var result = _result;
        var request = _builtRequest;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save a separate copy", SuggestedFileName = result.FileName });
            if (file?.TryGetLocalPath() is not { } path) return;
            if (_packagePath is not null) AtomicOutput.EnsureDifferentPath(_packagePath, path);
            await Task.Run(() => result.SaveCopy(path, request.SourcePath, request.ReplacementPath, cancellation.Token));
            C<TextBlock>("Status").Text = $"Saved verified copy to {path}";
        }
        catch (OperationCanceledException) { C<TextBlock>("Status").Text = "Save cancelled. The verified result is still available."; }
        catch (Exception ex) { C<TextBlock>("Status").Text = ex.Message; }
        finally { _cancellation = null; SetBusy(false); }
    }

    private void OnStage(object? sender, RoutedEventArgs e)
    {
        if (_result is null || !C<Button>("StageButton").IsEnabled) return;
        try { Close(_result.ReadForStaging()); }
        catch (Exception ex) { C<TextBlock>("Status").Text = ex.Message; }
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_cancellation is not null) { e.Cancel = true; _cancellation.Cancel(); }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _result?.Dispose();
        if (_packageScratch is not null && Directory.Exists(_packageScratch)) Directory.Delete(_packageScratch, recursive: true);
        base.OnClosed(e);
    }
}
