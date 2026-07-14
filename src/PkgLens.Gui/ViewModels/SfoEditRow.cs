using CommunityToolkit.Mvvm.ComponentModel;
using PkgLens.Core.Shared.Sfo;

namespace PkgLens.Gui.ViewModels;

/// <summary>An editable PARAM.SFO row for the editor dialog.</summary>
public sealed partial class SfoEditRow : ObservableObject
{
    public required string Key { get; init; }
    public required SfoFormat Format { get; init; }
    public uint MaxLength { get; init; }

    [ObservableProperty]
    private string _value = "";

    public bool IsInt => Format == SfoFormat.Int32;
    public string TypeLabel => IsInt ? "int" : "text";

    public static SfoEditRow From(SfoEntry e) => new()
    {
        Key = e.Key,
        Format = e.Format,
        MaxLength = e.MaxLength,
        Value = e.Value,
    };

    /// <summary>Builds a fresh row for a newly-added key. MaxLength 0 lets the writer size the field to the value.</summary>
    public static SfoEditRow NewKey(string key, bool isInt, string value) => new()
    {
        Key = key,
        Format = isInt ? SfoFormat.Int32 : SfoFormat.Utf8,
        MaxLength = 0,
        Value = value,
    };

    /// <summary>Converts back to a core <see cref="SfoEntry"/>. Assumes int values were validated.</summary>
    public SfoEntry ToEntry() => IsInt
        ? new SfoEntry { Key = Key, Format = Format, MaxLength = MaxLength, Value = Value, IntValue = uint.Parse(Value) }
        : new SfoEntry { Key = Key, Format = Format, MaxLength = MaxLength, Value = Value };
}
