using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public sealed record PkgReplacement(long Length, Func<Stream> OpenRead)
{
    public static PkgReplacement FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new(data.LongLength, () => new MemoryStream(data, writable: false));
    }

    public static PkgReplacement FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return new(new FileInfo(fullPath).Length,
            () => new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read));
    }
}
