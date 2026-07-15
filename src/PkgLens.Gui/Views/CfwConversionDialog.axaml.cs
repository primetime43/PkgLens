using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core.Shared;

namespace PkgLens.Gui.Views;

public sealed record CfwConversionDialogResult(CfwFirmwareTarget? FirmwareTarget);

public partial class CfwConversionDialog : Window
{
    private CheckBox _firmwareCheck = null!;
    private TextBox _firmwareBox = null!;
    private TextBlock _errorText = null!;

    public CfwConversionDialog() => InitializeComponent();

    public CfwConversionDialog(string sourcePath)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("SourceText")!.Text = sourcePath;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _firmwareCheck = this.FindControl<CheckBox>("FirmwareCheck")!;
        _firmwareBox = this.FindControl<TextBox>("FirmwareBox")!;
        _errorText = this.FindControl<TextBlock>("ErrorText")!;
    }

    private void OnFirmwareChanged(object? sender, RoutedEventArgs e) =>
        _firmwareBox.IsEnabled = _firmwareCheck.IsChecked == true;

    private void OnConvert(object? sender, RoutedEventArgs e)
    {
        CfwFirmwareTarget? target = null;
        if (_firmwareCheck.IsChecked == true)
        {
            string value = (_firmwareBox.Text ?? string.Empty).Trim();
            string[] parts = value.Split('.', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int major) ||
                !int.TryParse(parts[1], out int minor) || major is < 0 or > 15 || minor is < 0 or > 99)
            {
                _errorText.Text = "Enter firmware as major.minor, for example 4.00 or 4.91.";
                _errorText.IsVisible = true;
                return;
            }
            target = new CfwFirmwareTarget(major, minor);
        }

        Close(new CfwConversionDialogResult(target));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
