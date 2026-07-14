using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views;

/// <summary>Shows a read-only report for a content folder (from <c>GameFolderInfo.Describe</c>).</summary>
public partial class FolderInfoDialog : Window
{
    public FolderInfoDialog() => AvaloniaXamlLoader.Load(this);

    public FolderInfoDialog(string folderPath, string body) : this()
    {
        this.FindControl<TextBlock>("HeaderText")!.Text = folderPath;
        this.FindControl<TextBlock>("InfoText")!.Text = body;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
