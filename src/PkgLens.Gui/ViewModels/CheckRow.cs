using Avalonia.Media;
using PkgLens.Core;

namespace PkgLens.Gui.ViewModels;

/// <summary>A verification check formatted for display (colored mark + name + detail).</summary>
public sealed class CheckRow
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required string Mark { get; init; }
    public required IBrush Color { get; init; }

    private static readonly SolidColorBrush Green = new(Avalonia.Media.Color.Parse("#2FB872"));
    private static readonly SolidColorBrush Red = new(Avalonia.Media.Color.Parse("#E5484D"));
    private static readonly SolidColorBrush Gray = new(Avalonia.Media.Color.Parse("#9BA0AA"));

    public static CheckRow From(PkgCheck c) => new()
    {
        Name = c.Name,
        Detail = c.Detail,
        Mark = c.Status switch
        {
            PkgCheckStatus.Pass => "OK",
            PkgCheckStatus.Fail => "FAIL",
            _ => "SKIP",
        },
        Color = c.Status switch
        {
            PkgCheckStatus.Pass => Green,
            PkgCheckStatus.Fail => Red,
            _ => Gray,
        },
    };
}
