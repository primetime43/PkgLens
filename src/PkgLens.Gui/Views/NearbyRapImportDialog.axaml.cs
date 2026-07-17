using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PkgLens.Core.Ps3.Npd;

namespace PkgLens.Gui.Views;

public partial class NearbyRapImportDialog : Window
{
    public NearbyRapImportDialog() : this(new List<RapDiscoveryCandidate>(), string.Empty) { }

    public NearbyRapImportDialog(IReadOnlyList<RapDiscoveryCandidate> candidates, string libraryDirectory)
    {
        InitializeComponent();
        int valid = candidates.Count(candidate => candidate.IsValid);
        int invalid = candidates.Count - valid;
        this.FindControl<TextBlock>("SummaryText")!.Text = invalid == 0
            ? $"PkgLens found {valid} valid matching RAP file(s). Import them for automatic license resolution?"
            : $"PkgLens found {valid} valid and {invalid} invalid matching RAP file(s). Only valid files will be imported.";
        this.FindControl<TextBlock>("LibraryText")!.Text = $"Destination library: {libraryDirectory}";
        this.FindControl<DataGrid>("CandidatesGrid")!.ItemsSource = candidates.Select(NearbyRapRow.From).ToList();
        this.FindControl<Button>("ImportButton")!.IsEnabled = valid > 0;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    private void OnImport(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

public sealed record NearbyRapRow(string ContentId, string Status, string Detail)
{
    public static NearbyRapRow From(RapDiscoveryCandidate candidate) => new(
        candidate.ContentId,
        candidate.IsValid ? "Valid" : "Invalid",
        candidate.IsValid ? candidate.Path : $"{candidate.Path} — {candidate.Error}");
}
