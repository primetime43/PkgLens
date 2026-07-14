using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PkgLens.Core.Keys;

namespace PkgLens.Gui.Views;

/// <summary>
/// A tiny modal dialog that takes a pasted retail key. Closes with the parsed 16-byte key on
/// success, or null if cancelled. The caller is responsible for installing it.
/// </summary>
public partial class KeyImportDialog : Window
{
    private TextBox _keyBox = null!;
    private TextBlock _feedback = null!;

    public KeyImportDialog()
    {
        InitializeComponent();
        _keyBox = this.FindControl<TextBox>("KeyBox")!;
        _feedback = this.FindControl<TextBlock>("Feedback")!;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnLoadFromFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a key file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Key file") { Patterns = new[] { "*.key", "*.txt", "*.bin" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        try
        {
            byte[] key = KeyStore.ResolveKeyArgument(path);
            // Populate the box with the parsed key so the user can confirm before importing.
            _keyBox.Text = Convert.ToHexString(key);
            _feedback.Text = $"Loaded from {System.IO.Path.GetFileName(path)} — {KeyStore.Describe(key)}.";
            _feedback.IsVisible = true;
        }
        catch (Exception ex)
        {
            _feedback.Text = $"Could not read that key file: {ex.Message}";
            _feedback.IsVisible = true;
        }
    }

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        string text = _keyBox.Text ?? string.Empty;
        try
        {
            byte[] key = KeyStore.ParseKeyText(text);
            Close(key);
        }
        catch (Exception ex)
        {
            _feedback.Text = $"Invalid key: {ex.Message}. Expected 32 hex characters.";
            _feedback.IsVisible = true;
        }
    }
}
