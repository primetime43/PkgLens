using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core.Sfo;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

/// <summary>
/// Edits PARAM.SFO values. Closes with the edited entry list on Apply, or null on Cancel.
/// </summary>
public partial class SfoEditorDialog : Window
{
    private readonly List<SfoEditRow> _rows = new();
    private TextBlock _error = null!;

    public SfoEditorDialog() => InitializeComponent();

    public SfoEditorDialog(IReadOnlyList<SfoEntry> entries)
    {
        InitializeComponent();
        _rows = entries.Select(SfoEditRow.From).ToList();
        this.FindControl<ItemsControl>("Rows")!.ItemsSource = _rows;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _error = this.FindControl<TextBlock>("ErrorText")!;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var badInt = _rows.FirstOrDefault(r => r.IsInt && !uint.TryParse(r.Value, out _));
        if (badInt is not null)
        {
            _error.Text = $"'{badInt.Key}' must be a whole number (0–4294967295).";
            _error.IsVisible = true;
            return;
        }

        Close(_rows.Select(r => r.ToEntry()).ToList());
    }
}
