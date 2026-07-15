using System.IO;

namespace PkgLens.Gui.ViewModels;

public sealed record RecentPackageItem(string FilePath)
{
    public string FileName => Path.GetFileName(FilePath);
    public string DirectoryPath => Path.GetDirectoryName(FilePath) ?? string.Empty;
}
