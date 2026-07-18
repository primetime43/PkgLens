using System;
using System.IO;
using System.Text;

namespace PkgLens.Gui.Services;

internal static class Ps1ToolSettings
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens", "psxtract-path.txt");

    public static string? Load()
    {
        try
        {
            string path = File.ReadAllText(FilePath).Trim();
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicOutput.Write(FilePath, destination =>
            {
                using var writer = new StreamWriter(destination, new UTF8Encoding(false), 1024, leaveOpen: true);
                writer.Write(path);
            });
        }
        catch
        {
        }
    }
}
