using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PkgLens.Gui.Services;

internal static class RapSearchFolderSettings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens", "rap-search-folders");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return Array.Empty<string>();
            return File.ReadAllLines(FilePath)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void Save(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        try
        {
            string[] values = directories.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicOutput.Write(FilePath, stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
                foreach (string value in values)
                    writer.WriteLine(value);
            });
        }
        catch
        {
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
