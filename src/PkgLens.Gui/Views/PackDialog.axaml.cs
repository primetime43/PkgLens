using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core;
using PkgLens.Core.Models;

namespace PkgLens.Gui.Views;

/// <summary>
/// Collects pack options for building a .pkg from a content folder. Closes with a
/// <see cref="PackOptions"/> (with all fields set) on confirm, or null if cancelled. The source
/// folder is chosen before this dialog opens; the caller runs the build.
/// </summary>
public partial class PackDialog : Window
{
    private static readonly PkgContentType[] TypeChoices =
    {
        PkgContentType.GameExec, PkgContentType.GameData, PkgContentType.Theme,
        PkgContentType.Widget, PkgContentType.License, PkgContentType.Ps1Emu,
        PkgContentType.Psp, PkgContentType.Vsh, PkgContentType.Ps2Classic,
    };

    private TextBox _contentIdBox = null!;
    private TextBox _installDirBox = null!;
    private ComboBox _contentTypeBox = null!;
    private NumericUpDown _drmTypeBox = null!;
    private RadioButton _retailRadio = null!;
    private TextBlock _feedback = null!;

    // Parameterless ctor for the XAML previewer / loader.
    public PackDialog() : this(string.Empty, null) { }

    /// <param name="folder">The chosen source folder (display only).</param>
    /// <param name="inferred">A Fast-Pack plan used to pre-fill fields, or null if inference failed.</param>
    public PackDialog(string folder, PackPlan? inferred)
    {
        InitializeComponent();
        _contentIdBox = this.FindControl<TextBox>("ContentIdBox")!;
        _installDirBox = this.FindControl<TextBox>("InstallDirBox")!;
        _contentTypeBox = this.FindControl<ComboBox>("ContentTypeBox")!;
        _drmTypeBox = this.FindControl<NumericUpDown>("DrmTypeBox")!;
        _retailRadio = this.FindControl<RadioButton>("RetailRadio")!;
        _feedback = this.FindControl<TextBlock>("Feedback")!;

        this.FindControl<TextBlock>("FolderText")!.Text = folder;

        _contentTypeBox.ItemsSource = TypeChoices.Select(t => $"{t} (0x{(uint)t:X})").ToList();
        _contentTypeBox.SelectedIndex = 0; // GameExec

        if (inferred is not null)
        {
            _contentIdBox.Text = inferred.ContentId;
            _installDirBox.Text = inferred.InstallDirectory;
            _drmTypeBox.Value = inferred.DrmType;
            int idx = System.Array.FindIndex(TypeChoices, t => (uint)t == inferred.ContentType);
            if (idx >= 0) _contentTypeBox.SelectedIndex = idx;

            if (inferred.Notes.Count > 0)
            {
                var notes = this.FindControl<TextBlock>("NotesText")!;
                notes.Text = "Inferred: " + string.Join("; ", inferred.Notes);
                notes.IsVisible = true;
            }
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnPack(object? sender, RoutedEventArgs e)
    {
        string contentId = (_contentIdBox.Text ?? string.Empty).Trim();
        if (contentId.Length == 0)
        {
            Fail("A content id is required (e.g. UP0001-NPUB30910_00-EXAMPLE000000001).");
            return;
        }

        uint contentType = _contentTypeBox.SelectedIndex >= 0
            ? (uint)TypeChoices[_contentTypeBox.SelectedIndex]
            : (uint)PkgContentType.GameExec;

        string installDir = (_installDirBox.Text ?? string.Empty).Trim();

        Close(new PackOptions
        {
            ContentId = contentId,
            InstallDirectory = installDir.Length == 0 ? null : installDir,
            ContentType = contentType,
            DrmType = (uint)(_drmTypeBox.Value ?? 3),
            Finalization = _retailRadio.IsChecked == true
                ? PkgFinalization.Retail
                : PkgFinalization.Debug,
        });
    }

    private void Fail(string message)
    {
        _feedback.Text = message;
        _feedback.IsVisible = true;
    }
}
