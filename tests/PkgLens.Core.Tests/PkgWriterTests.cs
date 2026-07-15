using System.Collections.Generic;
using System.Linq;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class PkgWriterTests
{
    private static byte[] Sfo() => new SfoBuilder()
        .AddString("TITLE", "Repack Test")
        .AddString("TITLE_ID", "NPUB30910")
        .Build();

    [Fact]
    public void Repack_ReplacesFile_AndPreservesOthers()
    {
        byte[] after = Enumerable.Range(0, 777).Select(i => (byte)(i * 11 + 5)).ToArray();
        byte[] pkg = new SyntheticPkgBuilder()
            .AddDirectory("USRDIR")
            .AddFile("PARAM.SFO", Sfo())
            .AddFile("USRDIR/DATA.BIN", new byte[] { 1, 2, 3, 4, 5 })
            .AddFile("USRDIR/AFTER.BIN", after)
            .Build();

        var keys = new InMemoryKeyProvider();
        using var src = new MemoryStream(pkg);
        var info = PkgReader.Read(src, keys);
        var target = info.Entries.Single(e => e.Name == "USRDIR/DATA.BIN");

        // Replace with a larger payload (forces later offsets to shift).
        var newContent = Enumerable.Range(0, 2000).Select(i => (byte)(i * 7 + 3)).ToArray();
        var replacements = new Dictionary<PkgEntry, byte[]> { [target] = newContent };

        using var dst = new MemoryStream();
        PkgWriter.Repack(src, info, replacements, keys, dst);
        byte[] repacked = dst.ToArray();

        using var src2 = new MemoryStream(repacked);
        var info2 = PkgReader.Read(src2, keys);

        // Structure preserved.
        Assert.Equal(info.Entries.Count, info2.Entries.Count);
        Assert.NotNull(info2.Sfo);
        Assert.Equal("Repack Test", info2.Sfo!.Title);

        // Replaced content round-trips.
        var data2 = info2.Entries.Single(e => e.Name == "USRDIR/DATA.BIN");
        var readback = PkgReader.ExtractEntryBytes(src2, info2.Header, data2, keys);
        Assert.Equal(newContent, readback);

        // An unchanged file after the replacement moved to a new CTR offset and must be
        // decrypted from its old offset, then re-encrypted at the new one while streaming.
        var after2 = info2.Entries.Single(e => e.Name == "USRDIR/AFTER.BIN");
        Assert.Equal(after, PkgReader.ExtractEntryBytes(src2, info2.Header, after2, keys));

        // Header size fields consistent with the new file.
        Assert.Equal((ulong)repacked.Length, info2.Header.TotalSize);
    }

    [Fact]
    public void Repack_Retail_RoundTripsWithKey()
    {
        var builder = new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
            .AddFile("PARAM.SFO", Sfo())
            .AddFile("A.BIN", new byte[] { 9, 9, 9 });
        byte[] pkg = builder.Build();

        var keys = new InMemoryKeyProvider(builder.RetailAesKey);
        using var src = new MemoryStream(pkg);
        var info = PkgReader.Read(src, keys);
        var a = info.Entries.Single(e => e.Name == "A.BIN");

        var replacement = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        using var dst = new MemoryStream();
        PkgWriter.Repack(src, info, new Dictionary<PkgEntry, byte[]> { [a] = replacement }, keys, dst);

        using var src2 = new MemoryStream(dst.ToArray());
        var info2 = PkgReader.Read(src2, keys);
        var a2 = info2.Entries.Single(e => e.Name == "A.BIN");
        Assert.Equal(replacement, PkgReader.ExtractEntryBytes(src2, info2.Header, a2, keys));
        Assert.Equal("Repack Test", info2.Sfo?.Title);
    }

    [Fact]
    public void Repack_NoChanges_ReproducesReadablePackage()
    {
        byte[] pkg = new SyntheticPkgBuilder()
            .AddFile("PARAM.SFO", Sfo())
            .AddFile("ICON0.PNG", new byte[64])
            .Build();

        var keys = new InMemoryKeyProvider();
        using var src = new MemoryStream(pkg);
        var info = PkgReader.Read(src, keys);

        using var dst = new MemoryStream();
        PkgWriter.Repack(src, info, new Dictionary<PkgEntry, byte[]>(), keys, dst);

        using var src2 = new MemoryStream(dst.ToArray());
        var info2 = PkgReader.Read(src2, keys);
        Assert.Equal(info.Entries.Count, info2.Entries.Count);
        Assert.Equal("Repack Test", info2.Sfo?.Title);
        Assert.Equal("NPUB30910", info2.ContentId.TitleId);
    }

    [Fact]
    public void Repack_LargeOriginalEntry_IsReadInBoundedChunks()
    {
        byte[] payload = Enumerable.Range(0, 5 * 1024 * 1024 + 123)
            .Select(i => (byte)(i * 17 + 9))
            .ToArray();
        byte[] pkg = new SyntheticPkgBuilder()
            .AddFile("PARAM.SFO", Sfo())
            .AddFile("LARGE.BIN", payload)
            .Build();

        using var inner = new MemoryStream(pkg);
        using var source = new MaxReadSizeStream(inner, 1 << 20);
        var keys = new InMemoryKeyProvider();
        var info = PkgReader.Read(source, keys);

        using var destination = new MemoryStream();
        PkgWriter.Repack(source, info, new Dictionary<PkgEntry, byte[]>(), keys, destination);

        Assert.True(source.MaxObservedRead <= 1 << 20);
        using var repacked = new MemoryStream(destination.ToArray());
        var repackedInfo = PkgReader.Read(repacked, keys);
        var large = repackedInfo.Entries.Single(e => e.Name == "LARGE.BIN");
        Assert.Equal(payload, PkgReader.ExtractEntryBytes(repacked, repackedInfo.Header, large, keys));
    }

    [Fact]
    public void Repack_CancelledToken_StopsBeforeWriting()
    {
        byte[] pkg = new SyntheticPkgBuilder().AddFile("DATA.BIN", new byte[1024]).Build();
        using var source = new MemoryStream(pkg);
        var keys = new InMemoryKeyProvider();
        var info = PkgReader.Read(source, keys);
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PkgWriter.Repack(source, info, new Dictionary<PkgEntry, byte[]>(), keys, destination,
                cancellation.Token));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void Repack_ReportsByteProgress()
    {
        byte[] pkg = new SyntheticPkgBuilder().AddFile("DATA.BIN", new byte[2 * 1024 * 1024]).Build();
        using var source = new MemoryStream(pkg);
        var keys = new InMemoryKeyProvider();
        var info = PkgReader.Read(source, keys);
        using var destination = new MemoryStream();
        PkgOperationProgress last = default;
        var progress = new InlineProgress<PkgOperationProgress>(value => last = value);

        PkgWriter.Repack(source, info, new Dictionary<PkgEntry, byte[]>(), keys, destination,
            progress: progress);

        Assert.Equal(last.Total, last.Completed);
        Assert.Equal(100, last.Percent);
        Assert.Equal("DATA.BIN", last.Item);
    }

    private sealed class MaxReadSizeStream(Stream inner, int maximumRead) : Stream
    {
        public int MaxObservedRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            MaxObservedRead = Math.Max(MaxObservedRead, count);
            if (count > maximumRead)
                throw new InvalidOperationException($"Read request {count} exceeded {maximumRead} bytes.");
            return inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
