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
        byte[] pkg = new SyntheticPkgBuilder()
            .AddDirectory("USRDIR")
            .AddFile("PARAM.SFO", Sfo())
            .AddFile("USRDIR/DATA.BIN", new byte[] { 1, 2, 3, 4, 5 })
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
}
