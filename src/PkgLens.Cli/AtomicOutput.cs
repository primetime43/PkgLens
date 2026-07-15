namespace PkgLens.Cli;

internal static class AtomicOutput
{
    public static void EnsureDifferentPath(string input, string output)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), comparison))
            throw new IOException("The output path must be different from the input path.");
    }

    public static void Write(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        string temp = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup after a failed write */ }
        }
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        Write(path, destination => destination.Write(bytes));
    }
}
