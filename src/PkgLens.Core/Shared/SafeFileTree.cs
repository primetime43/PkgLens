namespace PkgLens.Core.Shared;

internal static class SafeFileTree
{
    public static IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo root)
    {
        ThrowIfLink(root);
        return EnumerateChildren(root);
    }

    public static bool ContainsFile(DirectoryInfo root, string fileName) =>
        Enumerate(root).OfType<FileInfo>().Any(file =>
            file.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    public static void ThrowIfLink(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            throw new PkgFormatException(
                $"Content folder contains a symbolic link or junction: {info.FullName}");
    }

    private static IEnumerable<FileSystemInfo> EnumerateChildren(DirectoryInfo directory)
    {
        foreach (var info in directory.EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            ThrowIfLink(info);
            yield return info;

            if (info is DirectoryInfo child)
                foreach (var descendant in EnumerateChildren(child))
                    yield return descendant;
        }
    }
}
