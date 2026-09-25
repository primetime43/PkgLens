using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PkgLens.Gui.Views;

public enum UnsavedChangesChoice { Cancel, Discard, Save }

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog() => AvaloniaXamlLoader.Load(this);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Cancel);
    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Discard);
    private void OnSave(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Save);
}
