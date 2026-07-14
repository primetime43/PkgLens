using System;
using PkgLens.Core;
using PkgLens.Core.Npd.Psp;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// Covers NPUMDIMG detection and the safety gates. The full ISO decrypt is not yet exercised: the
/// version-key derivation from the RAP for PSP NPUMDIMG is still being pinned down, and the decryptor
/// deliberately refuses to emit an ISO until the header validates (so it never produces garbage).
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
    public void ParseHeader_RapLicensed_WithoutKlic_ThrowsKeyError()
    {
        // np_flags bit 1 set → RAP-licensed; no klicensee supplied must be a key error, not a crash.
        Assert.Throws<PkgKeyException>(() => NpumdImg.ParseHeader(Header(2), null));
    }

    [Fact]
    public void ParseHeader_NotNpumdImg_ThrowsFormat()
    {
        Assert.Throws<PkgFormatException>(() => NpumdImg.ParseHeader(new byte[0x100], null));
    }
}
