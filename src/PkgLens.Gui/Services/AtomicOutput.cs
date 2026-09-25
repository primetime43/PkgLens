using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PkgLens.Gui.Services;

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

    public static void Write(string path, Action<Stream> write, bool overwrite = true, Action<string>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        string fullPath = Path.GetFullPath(path);
        string temp = TempPath(fullPath);
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true);
            }
            validate?.Invoke(temp);
            File.Move(temp, fullPath, overwrite);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        Write(path, destination => destination.Write(bytes));
    }

    public static async Task WriteAllBytesAsync(string path, byte[] data,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        string temp = TempPath(fullPath);
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await file.WriteAsync(data, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static async Task WriteAllTextAsync(string path, string content,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        string temp = TempPath(fullPath);
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string TempPath(string fullPath) => Path.Combine(
        Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup after a failed write */ }
    }
}
