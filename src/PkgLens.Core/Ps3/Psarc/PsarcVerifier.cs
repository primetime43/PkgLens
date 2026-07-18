namespace PkgLens.Core.Ps3.Psarc;

public static class PsarcVerifier
{
    public static PsarcArchiveInfo Verify(string path, CancellationToken cancellationToken = default,
        IProgress<PsarcProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PsarcArchiveInfo archive = PsarcReader.Read(path);
        long total = archive.Entries.Sum(entry => checked((long)entry.Length));
        long completed = 0;
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        uint[] blockLengths = PsarcReader.ReadBlockLengths(source, archive.Header);
        foreach (PsarcEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long itemBase = completed;
            var itemProgress = new PsarcProgressRelay(value =>
                progress?.Report(new PsarcProgress(itemBase + value.Completed, total, entry.Path)));
            var raw = new PsarcReader.RawEntry(entry.NameDigest, entry.FirstBlockIndex, entry.Length, entry.DataOffset);
            PsarcReader.ExtractRawEntry(source, raw, blockLengths, archive.Header.BlockSize,
                archive.Header.Compression, Stream.Null, cancellationToken, itemProgress, entry.Path);
            completed += checked((long)entry.Length);
            progress?.Report(new PsarcProgress(completed, total, entry.Path));
        }
        return archive;
    }
}
