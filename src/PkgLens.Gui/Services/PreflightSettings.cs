using System;
using System.IO;
using System.Text;

namespace PkgLens.Gui.Services;

internal static class PreflightSettings
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens", "preflight-dialog");

    public static bool Load()
    {
        try
        {
            return File.Exists(FilePath) &&
                   string.Equals(File.ReadAllText(FilePath).Trim(), "on", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Save(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicOutput.Write(FilePath, stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
                writer.Write(enabled ? "on" : "off");
            });
        }
        catch
        {
        }
    }
}
