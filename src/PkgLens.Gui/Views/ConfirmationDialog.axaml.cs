using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog() : this("Confirm operation", string.Empty, "Continue") { }

    public ConfirmationDialog(string heading, string message, string confirmText)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("HeadingText")!.Text = heading;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<Button>("ConfirmButton")!.Content = confirmText;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
