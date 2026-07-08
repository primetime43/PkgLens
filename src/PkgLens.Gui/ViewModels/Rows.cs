namespace PkgLens.Gui.ViewModels;

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
