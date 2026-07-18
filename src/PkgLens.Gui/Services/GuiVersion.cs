using System.Reflection;

namespace PkgLens.Gui.Services;

public static class GuiVersion
{
    public static string Value { get; } =
        typeof(GuiVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(GuiVersion).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public static string ProductName => $"PkgLens {Value}";
}
