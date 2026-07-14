using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using PkgLens.Core;
using PkgLens.Core.Pbp;
using Xunit;

namespace PkgLens.Core.Tests;

public class PbpArchiveTests
{
    /// <summary>Builds a synthetic PBP from the eight fixed slots (empty ones collapse to zero size).</summary>
    private static byte[] BuildPbp(params (int slot, byte[] data)[] sections)
    {
        var slots = new byte[8][];
        for (int i = 0; i < 8; i++) slots[i] = System.Array.Empty<byte>();
        foreach (var (slot, data) in sections) slots[slot] = data;

        var offsets = new uint[8];
        uint cursor = PbpArchive.HeaderSize;
        for (int i = 0; i < 8; i++) { offsets[i] = cursor; cursor += (uint)slots[i].Length; }

        var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[PbpArchive.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, PbpArchive.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[0x04..], 0x00010000);
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(hdr[(0x08 + i * 4)..], offsets[i]);
        ms.Write(hdr);
        foreach (var s in slots) ms.Write(s, 0, s.Length);
        return ms.ToArray();
    }

    [Fact]
    public void Parse_ExposesNonEmptySections_WithCorrectSizes()
    {
        byte[] sfo = Encoding.ASCII.GetBytes("SFO-CONTENT");
        byte[] psp = Encoding.ASCII.GetBytes("~PSP-fake-executable");
        byte[] psar = Encoding.ASCII.GetBytes("PSAR-data-blob-here");
        byte[] pbp = BuildPbp((0, sfo), (6, psp), (7, psar)); // PARAM.SFO, DATA.PSP, DATA.PSAR

        using var s = new MemoryStream(pbp);
        var arc = PbpArchive.Parse(s);

        Assert.Equal(3, arc.Entries.Count);
        Assert.Equal(new[] { "PARAM.SFO", "DATA.PSP", "DATA.PSAR" }, arc.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(sfo.Length, arc.Entries[0].Size);
        Assert.Equal(psar.Length, arc.Entries.Single(e => e.Name == "DATA.PSAR").Size);
    }

    [Fact]
    public void Read_RoundTripsSectionBytes()
    {
        byte[] psp = Encoding.ASCII.GetBytes("~PSP-executable-payload");
        byte[] pbp = BuildPbp((6, psp));

        using var s = new MemoryStream(pbp);
        var arc = PbpArchive.Parse(s);
        var entry = arc.Entries.Single(e => e.Name == "DATA.PSP");

        Assert.Equal(psp, PbpArchive.Read(s, entry));

        using var dest = new MemoryStream();
        PbpArchive.Extract(s, entry, dest);
        Assert.Equal(psp, dest.ToArray());
    }

    [Fact]
    public void IsPbp_MatchesMagicOnly()
    {
        Assert.True(PbpArchive.IsPbp(BuildPbp((0, new byte[4]))));
        Assert.False(PbpArchive.IsPbp(Encoding.ASCII.GetBytes("\0PGD")));
    }

    [Fact]
    public void Parse_Truncated_Throws()
    {
        Assert.Throws<PkgFormatException>(() => PbpArchive.Parse(new MemoryStream(new byte[8])));
    }
}
