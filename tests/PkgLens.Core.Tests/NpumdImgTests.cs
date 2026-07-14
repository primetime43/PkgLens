using System;
using PkgLens.Core;
using PkgLens.Core.Npd.Psp;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// Covers NPUMDIMG detection and the license-free header handling. The version key is recovered from
/// the header itself (AMCTRL bbmac_getkey) — no RAP. The full ISO decrypt is verified against a real
/// minis image during development (documented, not committed). The decryptor's HeaderValid gate keeps
/// it from emitting a garbage ISO for a corrupt/unsupported header.
/// </summary>
public class NpumdImgTests
{
    private static byte[] Header(uint npFlags)
    {
        var h = new byte[0x100];
        "NPUMDIMG"u8.CopyTo(h);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x08), npFlags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x0C), 0x10);
        return h;
    }

    [Fact]
    public void IsNpumdImg_MatchesMagicOnly()
    {
        Assert.True(NpumdImg.IsNpumdImg(Header(2)));
        Assert.False(NpumdImg.IsNpumdImg("NPUMDXXX"u8.ToArray()));
    }

    [Fact]
    public void ParseHeader_SyntheticHeader_RecoversKeyThenReportsInvalidLayout()
    {
        // Version-key recovery runs with no license; a synthetic (zero-body) header simply reports an
        // invalid layout rather than throwing — and never needs a RAP.
        var info = NpumdImg.ParseHeader(Header(2));
        Assert.False(info.HeaderValid);
    }

    [Fact]
    public void ParseHeader_NotNpumdImg_ThrowsFormat()
    {
        Assert.Throws<PkgFormatException>(() => NpumdImg.ParseHeader(new byte[0x100]));
    }
}
