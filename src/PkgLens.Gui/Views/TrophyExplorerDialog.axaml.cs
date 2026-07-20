using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using PkgLens.Core.Ps3.Trophy;

namespace PkgLens.Gui.Views;

public sealed class TrophyDisplayRow : IDisposable
{
    public TrophyDisplayRow(TrophyItem trophy)
    {
        Trophy = trophy;
        Icon = TrophyExplorerDialog.TryCreateBitmap(trophy.Icon);
    }

    public TrophyItem Trophy { get; }
    public int Id => Trophy.Id;
    public string IdText => Trophy.Id.ToString("D3");
    public string Name => Trophy.Name;
    public string GradeText => Trophy.Grade.ToString();
    public string HiddenText => Trophy.IsHidden ? "Yes" : "No";
    public Bitmap? Icon { get; }

    public void Dispose() => Icon?.Dispose();
}

/// <summary>Read-only browser for a TROPHY.TRP's set metadata, grade totals, descriptions, and PNG artwork.</summary>
public partial class TrophyExplorerDialog : Window
{
    private readonly List<TrophyDisplayRow> _rows = new();
    private Bitmap? _artwork;

    private Image _gameArtwork = null!;
    private Image _selectedIcon = null!;
    private TextBlock _selectedName = null!;
    private TextBlock _selectedGrade = null!;
    private TextBlock _selectedDescription = null!;
    private TextBlock _selectedHidden = null!;
    private DataGrid _grid = null!;

    public TrophyExplorerDialog() => InitializeComponent();

    public TrophyExplorerDialog(TrophySet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        InitializeComponent();

        Title = $"Trophy Explorer — {set.Name}";
        this.FindControl<TextBlock>("SetNameText")!.Text = set.Name;
        this.FindControl<TextBlock>("SetIdText")!.Text = set.Id;
        this.FindControl<TextBlock>("SetDescriptionText")!.Text = set.Description;
        this.FindControl<TextBlock>("CountsText")!.Text =
            $"{set.Trophies.Count} total  ·  {set.PlatinumCount} platinum  ·  {set.GoldCount} gold  ·  " +
            $"{set.SilverCount} silver  ·  {set.BronzeCount} bronze";
        this.FindControl<TextBlock>("ArchiveStatusText")!.Text = set.ChecksumVerified
            ? $"TRP v{set.ArchiveVersion} · SHA-1 verified · {set.MetadataFileName}"
            : $"TRP v{set.ArchiveVersion} · no archive checksum · {set.MetadataFileName}";

        _artwork = TryCreateBitmap(set.Artwork);
        _gameArtwork.Source = _artwork;
        _rows.AddRange(set.Trophies.Select(trophy => new TrophyDisplayRow(trophy)));
        _grid.ItemsSource = _rows;
        if (_rows.Count > 0)
        {
            _grid.SelectedIndex = 0;
            ShowSelection(_rows[0]);
        }
        else
            ShowSelection(null);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _gameArtwork = this.FindControl<Image>("GameArtwork")!;
        _selectedIcon = this.FindControl<Image>("SelectedTrophyIcon")!;
        _selectedName = this.FindControl<TextBlock>("SelectedTrophyName")!;
        _selectedGrade = this.FindControl<TextBlock>("SelectedTrophyGrade")!;
        _selectedDescription = this.FindControl<TextBlock>("SelectedTrophyDescription")!;
        _selectedHidden = this.FindControl<TextBlock>("SelectedTrophyHidden")!;
        _grid = this.FindControl<DataGrid>("TrophyGrid")!;
        Closed += (_, _) => DisposeImages();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ShowSelection(_grid.SelectedItem as TrophyDisplayRow);

    private void ShowSelection(TrophyDisplayRow? row)
    {
        _selectedIcon.Source = row?.Icon;
        _selectedName.Text = row?.Name ?? "No trophies found";
        _selectedGrade.Text = row is null ? string.Empty : $"#{row.IdText} · {row.GradeText}";
        _selectedDescription.Text = row?.Trophy.Description ?? string.Empty;
        _selectedHidden.Text = row is null ? string.Empty : row.Trophy.IsHidden
            ? "Hidden trophy"
            : "Visible trophy";
    }

    internal static Bitmap? TryCreateBitmap(byte[]? data)
    {
        if (data is not { Length: > 0 })
            return null;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private void DisposeImages()
    {
        _artwork?.Dispose();
        _artwork = null;
        foreach (TrophyDisplayRow row in _rows)
            row.Dispose();
        _rows.Clear();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
