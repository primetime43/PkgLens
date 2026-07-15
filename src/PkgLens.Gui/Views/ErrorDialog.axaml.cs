using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class ErrorDialog : Window
{
    public ErrorDialog() => InitializeComponent();

    public ErrorDialog(GuiErrorReport error) : this()
    {
        Title = error.Title;
        this.FindControl<TextBlock>("MessageText")!.Text = error.Message;
        this.FindControl<TextBlock>("DetailsText")!.Text = error.Details;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
