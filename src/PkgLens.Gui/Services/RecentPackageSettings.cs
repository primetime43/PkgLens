using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PkgLens.Gui.Services;

internal static class RecentPackageSettings
{
    public const int MaximumCount = 10;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens", "recent-packages.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return Array.Empty<string>();

            string[] paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? Array.Empty<string>();
            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(PathComparer)
                .Take(MaximumCount)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void Save(IEnumerable<string> paths)
    {
        try
        {
            string directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(paths.Take(MaximumCount));
            AtomicOutput.Write(FilePath, destination =>
            {
                using var writer = new StreamWriter(destination, new UTF8Encoding(false), 1024, leaveOpen: true);
                writer.Write(json);
            });
        }
        catch
        {
        }
    }

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
