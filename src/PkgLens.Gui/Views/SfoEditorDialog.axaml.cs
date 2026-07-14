using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core.Shared.Sfo;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

/// <summary>
/// Edits PARAM.SFO: change values, add keys, or remove keys. Closes with the edited entry list on
/// Apply, or null on Cancel.
/// </summary>
public partial class SfoEditorDialog : Window
{
    private readonly ObservableCollection<SfoEditRow> _rows = new();
    private TextBlock _error = null!;
    private TextBox _newKeyBox = null!;
    private ComboBox _newKeyType = null!;
    private TextBox _newKeyValue = null!;

    public SfoEditorDialog() => InitializeComponent();

    public SfoEditorDialog(IReadOnlyList<SfoEntry> entries)
    {
        InitializeComponent();
        foreach (var e in entries)
            _rows.Add(SfoEditRow.From(e));
        this.FindControl<ItemsControl>("Rows")!.ItemsSource = _rows;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _error = this.FindControl<TextBlock>("ErrorText")!;
        _newKeyBox = this.FindControl<TextBox>("NewKeyBox")!;
        _newKeyType = this.FindControl<ComboBox>("NewKeyType")!;
        _newKeyValue = this.FindControl<TextBox>("NewKeyValue")!;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private void OnRemoveRow(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SfoEditRow row })
            _rows.Remove(row);
    }

    private void OnAddKey(object? sender, RoutedEventArgs e)
    {
        string key = (_newKeyBox.Text ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            ShowError("Enter a key name to add.");
            return;
        }
        if (_rows.Any(r => string.Equals(r.Key, key, StringComparison.Ordinal)))
        {
            ShowError($"'{key}' already exists — edit or remove the existing row instead.");
            return;
        }

        bool isInt = _newKeyType.SelectedIndex == 1;
        string value = (_newKeyValue.Text ?? string.Empty).Trim();
        if (isInt && !uint.TryParse(value, out _))
        {
            ShowError($"'{key}' is an int key — its value must be a whole number (0–4294967295).");
            return;
        }

        _rows.Add(SfoEditRow.NewKey(key, isInt, isInt ? value : (_newKeyValue.Text ?? string.Empty)));
        _newKeyBox.Text = "";
        _newKeyValue.Text = "";
        _error.IsVisible = false;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var badInt = _rows.FirstOrDefault(r => r.IsInt && !uint.TryParse(r.Value, out _));
        if (badInt is not null)
        {
            ShowError($"'{badInt.Key}' must be a whole number (0–4294967295).");
            return;
        }

        Close(_rows.Select(r => r.ToEntry()).ToList());
    }
}
