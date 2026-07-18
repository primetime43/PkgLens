using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        Title = $"About {GuiVersion.ProductName}";
        this.FindControl<TextBlock>("VersionText")!.Text = $"Version {GuiVersion.Value}";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
