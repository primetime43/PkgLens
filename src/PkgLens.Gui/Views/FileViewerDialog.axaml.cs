using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace PkgLens.Gui.Views;

/// <summary>
/// A read-only viewer for a single entry: renders images, shows decoded text, or a hex dump.
/// </summary>
public partial class FileViewerDialog : Window
{
    private const int MaxHexBytes = 256 * 1024;

    private readonly string _name = "";
    private readonly byte[] _data = Array.Empty<byte>();
    private bool _showText;

    private TextBlock _header = null!;
    private ToggleButton _textToggle = null!;
    private Image _preview = null!;
    private ScrollViewer _imageScroll = null!;
    private TextBox _textArea = null!;

    // Parameterless ctor for the XAML designer.
    public FileViewerDialog()
    {
        InitializeComponent();
        Wire();
    }

    public FileViewerDialog(string name, byte[] data)
    {
        InitializeComponent();
        Wire();

        _name = name;
        _data = data;

        _header.Text = $"{name}  ·  {data.Length:n0} bytes";
        Title = $"View — {name}";

        if (TryLoadImage(data, out var bmp))
        {
            _preview.Source = bmp;
            _imageScroll.IsVisible = true;
        }
        else
        {
            _textToggle.IsVisible = true;
            _showText = LooksLikeText(data);
            _textToggle.IsChecked = _showText;
            _textArea.IsVisible = true;
            RenderText();
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Wire()
    {
        _header = this.FindControl<TextBlock>("HeaderText")!;
        _textToggle = this.FindControl<ToggleButton>("TextToggle")!;
        _preview = this.FindControl<Image>("Preview")!;
        _imageScroll = this.FindControl<ScrollViewer>("ImageScroll")!;
        _textArea = this.FindControl<TextBox>("TextArea")!;
    }

    private void OnToggleText(object? sender, RoutedEventArgs e)
    {
        _showText = _textToggle.IsChecked == true;
        RenderText();
    }

    private void RenderText() => _textArea.Text = _showText ? DecodeText(_data) : HexDump(_data);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnExtract(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Extract file to…",
            SuggestedFileName = _name,
        });
        if (file?.TryGetLocalPath() is { } dest)
        {
            try { await File.WriteAllBytesAsync(dest, _data); }
            catch (Exception ex) { _header.Text = $"Extract failed: {ex.Message}"; }
        }
    }

    private static bool TryLoadImage(byte[] data, out Bitmap? bitmap)
    {
        bitmap = null;
        try
        {
            using var ms = new MemoryStream(data);
            bitmap = new Bitmap(ms);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Heuristic: mostly-printable and no NULs in the first block.</summary>
    private static bool LooksLikeText(byte[] data)
    {
        int n = Math.Min(data.Length, 4096);
        if (n == 0) return false;
        int printable = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = data[i];
            if (b == 0) return false;
            if (b == 9 || b == 10 || b == 13 || (b >= 32 && b < 127)) printable++;
        }
        return printable >= n * 0.85;
    }

    private static string DecodeText(byte[] data)
    {
        int n = Math.Min(data.Length, MaxHexBytes);
        string text = Encoding.UTF8.GetString(data, 0, n);
        return data.Length > n ? text + $"\n\n… {data.Length - n:n0} more bytes not shown." : text;
    }

    private static string HexDump(byte[] data)
    {
        int n = Math.Min(data.Length, MaxHexBytes);
        var sb = new StringBuilder(n / 16 * 78 + 64);
        for (int i = 0; i < n; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++)
            {
                sb.Append(i + j < n ? data[i + j].ToString("X2") + " " : "   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < n; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.Append('\n');
        }
        if (data.Length > n)
            sb.Append($"\n… {data.Length - n:n0} more bytes not shown.");
        return sb.ToString();
    }
}
