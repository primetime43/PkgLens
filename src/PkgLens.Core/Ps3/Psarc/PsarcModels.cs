namespace PkgLens.Core.Ps3.Psarc;

[Flags]
public enum PsarcArchiveFlags : uint
{
    RelativePaths = 0,
    IgnoreCase = 1,
    AbsolutePaths = 2,
    Encrypted = 4,
}

public sealed record PsarcHeader(
    ushort MajorVersion,
    ushort MinorVersion,
    string Compression,
    uint DataOffset,
    uint TocEntrySize,
    uint EntryCount,
    uint BlockSize,
    PsarcArchiveFlags Flags,
    int BlockLengthSize);

public sealed record PsarcEntry(
    int Index,
    string Path,
    byte[] NameDigest,
    uint FirstBlockIndex,
    ulong Length,
    ulong DataOffset,
    ulong StoredLength,
    int BlockCount)
{
    public bool IsCompressed => StoredLength < Length;
}

public sealed record PsarcArchiveInfo(PsarcHeader Header, IReadOnlyList<PsarcEntry> Entries)
{
    public ulong TotalUncompressedBytes => Entries.Aggregate<PsarcEntry, ulong>(0, (total, entry) => total + entry.Length);
}

public sealed record PsarcReplacement(long Length, Func<Stream> OpenRead)
{
    public static PsarcReplacement FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new(data.LongLength, () => new MemoryStream(data, writable: false));
    }

    public static PsarcReplacement FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        return new(new FileInfo(fullPath).Length,
            () => new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read));
    }
}

public readonly record struct PsarcProgress(long Completed, long Total, string? Item = null)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}

internal sealed class PsarcProgressRelay(Action<PsarcProgress> report) : IProgress<PsarcProgress>
{
    public void Report(PsarcProgress value) => report(value);
}
