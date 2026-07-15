using System;
using System.IO;
using System.Text;
using Avalonia.Styling;

namespace PkgLens.Gui.Services;

/// <summary>
/// Loads/saves the user's chosen theme (System / Light / Dark) to a small file in ~/.pkglens so
/// the choice persists across runs. UI concern only — no package/crypto logic here.
/// </summary>
public static class ThemeSettings
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens", "theme");

    public static ThemeVariant Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Parse(File.ReadAllText(FilePath).Trim());
        }
        catch { /* fall through to default */ }
        return ThemeVariant.Default;
    }

    public static void Save(ThemeVariant variant)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            AtomicOutput.Write(FilePath, stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
                writer.Write(Name(variant));
            });
        }
        catch { /* non-fatal: theme just won't persist */ }
    }

    public static string Name(ThemeVariant variant) =>
        variant == ThemeVariant.Light ? "light" :
        variant == ThemeVariant.Dark ? "dark" : "system";

    private static ThemeVariant Parse(string value) => value.ToLowerInvariant() switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
