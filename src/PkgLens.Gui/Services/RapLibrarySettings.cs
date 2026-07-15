using System;
using System.IO;
using System.Text;

namespace PkgLens.Gui.Services;

internal static class RapLibrarySettings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens", "rap-library");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string value = File.ReadAllText(FilePath).Trim();
            return value.Length == 0 ? null : Path.GetFullPath(value);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                File.Delete(FilePath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicOutput.Write(FilePath, stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
                writer.Write(Path.GetFullPath(directory));
            });
        }
        catch
        {
        }
    }
}
