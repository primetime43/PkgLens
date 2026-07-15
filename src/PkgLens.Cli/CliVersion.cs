using System.Reflection;

namespace PkgLens.Cli;

internal static class CliVersion
{
    public static string Value =>
        typeof(CliVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CliVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
