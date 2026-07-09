using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using PkgLens.Core.Npd;
using Xunit;

namespace PkgLens.Core.Tests;

public class NpdTests
{
    [Fact]
    public void RapToKlicensee_MatchesKnownVector()
    {
        // Verified end-to-end: this RAP decrypts a real retail EDAT (Black Ops III DLC).
        var rap = Convert.FromHexString("879B800E95D0C969F94290BE22026D50");
        var klic = NpdKeys.RapToKlicensee(rap);
        Assert.Equal("A3588ACC2CA8C0AA50AC33D3D842A656", Convert.ToHexString(klic));
    }

    [Fact]
    public void ParseHeader_ReadsNpdAndEdatFields()
    {
        var buf = BuildHeader(version: 4, license: 2, flags: 0x0000003C, blockSize: 0x4000, fileSize: 46,
            contentId: "UP0002-BLUS31527_00-BLACKOPS3MAPPAK1");
        using var ms = new MemoryStream(buf);

        var npd = EdatFile.ParseHeader(ms);
        Assert.Equal(4, npd.Version);
        Assert.Equal(2, npd.License);
        Assert.Equal("Local", npd.LicenseText);
        Assert.Equal("UP0002-BLUS31527_00-BLACKOPS3MAPPAK1", npd.ContentId);
        Assert.Equal(0x4000, npd.BlockSize);
        Assert.Equal(46, npd.FileSize);
        Assert.False(npd.IsSdat);
        Assert.True(npd.NeedsKlicensee); // licensed (type 2), not free
    }

    [Fact]
    public void ParseHeader_DetectsSdatAndFree()
    {
        var sdat = BuildHeader(4, 3, flags: 0x01000000, blockSize: 0x4000, fileSize: 10, contentId: "X");
        using var s = new MemoryStream(sdat);
        var npd = EdatFile.ParseHeader(s);
        Assert.True(npd.IsSdat);
        Assert.False(npd.NeedsKlicensee); // SDAT needs no klicensee

        var free = BuildHeader(4, 3, flags: 0x0000003C, blockSize: 0x4000, fileSize: 10, contentId: "X");
        using var f = new MemoryStream(free);
        var npdFree = EdatFile.ParseHeader(f);
        Assert.True(npdFree.IsFree);
        Assert.False(npdFree.NeedsKlicensee); // free uses the built-in klicensee
    }

    [Fact]
    public void ParseHeader_BadMagic_Throws()
    {
        Assert.Throws<PkgLens.Core.PkgFormatException>(() => EdatFile.ParseHeader(new MemoryStream(new byte[0x90])));
    }

    private static byte[] BuildHeader(int version, int license, uint flags, int blockSize, long fileSize, string contentId)
    {
        var b = new byte[0x90];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x00), 0x4E504400); // "NPD\0"
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x04), (uint)version);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x08), (uint)license);
        Encoding.ASCII.GetBytes(contentId).CopyTo(b.AsSpan(0x10));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x80), flags);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x84), (uint)blockSize);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x88), (ulong)fileSize);
        return b;
    }
}
