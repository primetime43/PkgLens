namespace PkgLens.Gui.ViewModels;

using PkgLens.Core.Ps3.Npd;

/// <summary>A label/value row for the metadata table.</summary>
public sealed class MetadataRow
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

/// <summary>A key/value row for the PARAM.SFO table.</summary>
public sealed class SfoRow
{
    public required string Key { get; init; }
    public required string Format { get; init; }
    public required string Value { get; init; }
}

/// <summary>Display row for one entry in the RAP library manager.</summary>
public sealed class RapStoreRow
{
    private RapStoreRow(RapStoreEntry entry) => Entry = entry;

    internal RapStoreEntry Entry { get; }
    public string ContentId => Entry.ContentId;
    public string Status => Entry.IsValid ? "Valid" : "Invalid";
    public string Size => $"{Entry.Size} bytes";
    public string Detail => Entry.Error ?? Entry.Path;

    internal static RapStoreRow From(RapStoreEntry entry) => new(entry);
}

/// <summary>Display row for one non-secret entry in the klicensee library manager.</summary>
public sealed class KlicenseeStoreRow
{
    private KlicenseeStoreRow(KlicenseeStoreEntry entry) => Entry = entry;

    internal KlicenseeStoreEntry Entry { get; }
    public string ContentId => Entry.ContentId ?? "—";
    public string TitleId => Entry.TitleId;
    public string FileName => Entry.FileName ?? "Any";
    public string License => Entry.LicenseType?.ToString() ?? "Any";
    public bool IsBundled => Entry.Source == "bundled catalog";
    public bool CanRemove => !IsBundled;
    public string Source => IsBundled ? "Bundled" : Entry.Source;
    public string Fingerprint => Entry.Fingerprint;
    public string Added => IsBundled ? "Built in" : Entry.AddedUtc.ToLocalTime().ToString("g");

    internal static KlicenseeStoreRow From(KlicenseeStoreEntry entry) => new(entry);
}
