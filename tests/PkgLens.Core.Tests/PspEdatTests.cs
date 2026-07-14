using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using PkgLens.Core;
using PkgLens.Core.Npd.Psp;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// PSP EDAT ("\0PSPEDAT") detection, header parsing, and graceful-failure behavior. The full
/// AMCTRL/KIRK/PGD decrypt was verified end-to-end against a real fixed-key PSP EDAT
/// (GTA:LCS DOCINFO.EDAT) whose internal BBMac checks passed — a cryptographic proof of correctness
/// that can't be committed here (copyrighted). These tests cover the plumbing and the error paths
/// with synthetic buffers (no real keys or content beyond the public constants in Core).
/// </summary>
public class PspEdatTests
{
    /// <summary>Builds a minimal PSP EDAT wrapping a PGD with the given drm_type and random body.</summary>
    private static byte[] BuildPspEdat(int edatDrmType, uint pgdDrmType, string contentId, int seed)
    {
        const int headerSize = 0x80;
        const int pgdBody = 0x100;
        var b = new byte[headerSize + pgdBody];

        Encoding.ASCII.GetBytes("\0PSPEDAT").CopyTo(b, 0); // note: leading NUL then PSPEDAT
        b[0] = 0x00;
        Encoding.ASCII.GetBytes("PSPEDAT").CopyTo(b, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x08), (ushort)edatDrmType);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x0C), headerSize);
        Encoding.ASCII.GetBytes(contentId).CopyTo(b, 0x10);

        // PGD at headerSize.
        b[headerSize + 0] = 0x00;
        Encoding.ASCII.GetBytes("PGD").CopyTo(b, headerSize + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(headerSize + 4), 1);           // key_index
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(headerSize + 8), pgdDrmType);  // drm_type

        // Deterministic pseudo-random body (varied but no crypto strength needed for these tests).
        for (int i = headerSize + 0x10; i < b.Length; i++)
            b[i] = (byte)((i * 31 + seed) & 0xFF);
        return b;
    }

    [Fact]
    public void IsPspEdat_DetectsMagic()
    {
        byte[] edat = BuildPspEdat(2, 1, "UP0001-ULUS12345_00-EXAMPLEPSP000001", seed: 1);
        Assert.True(PspEdatFile.IsPspEdat(edat));
        Assert.True(PspEdatFile.IsPspEdat(new MemoryStream(edat)));

        // NPD ("NPD\0") and arbitrary data are not PSP EDATs.
        var npd = new byte[16];
        Encoding.ASCII.GetBytes("NPD\0").CopyTo(npd, 0);
        Assert.False(PspEdatFile.IsPspEdat(npd));
        Assert.False(PspEdatFile.IsPspEdat(new byte[8]));
    }

    [Fact]
    public void ParseHeader_ReadsFields()
    {
        byte[] edat = BuildPspEdat(edatDrmType: 2, pgdDrmType: 1, "UP1004-ULUS10041_00-GPCGRANDTH000001", seed: 3);
        var info = PspEdatFile.ParseHeader(edat);

        Assert.Equal(2, info.DrmType);
        Assert.Equal(0x80, info.HeaderSize);
        Assert.Equal("UP1004-ULUS10041_00-GPCGRANDTH000001", info.ContentId);
        Assert.True(info.DrmFreeOrLocal);
    }

    [Fact]
    public void Decrypt_CorruptedFixedKeyPgd_FailsMacGracefully()
    {
        // A well-formed wrapper around random PGD bytes must fail the BBMac check with a clean
        // PkgFormatException, never a crash or garbage output.
        byte[] edat = BuildPspEdat(edatDrmType: 2, pgdDrmType: 1, "UP0001-ULUS00001_00-TESTPSP00000001", seed: 7);
        var ex = Assert.Throws<PkgFormatException>(() => PspEdatFile.DecryptToArray(new MemoryStream(edat)));
        Assert.Contains("MAC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_FuseBoundPgd_ReportsUnsupported()
    {
        // drm_type 2 is bound to the originating console's fuse key — must be rejected with a clear message.
        byte[] edat = BuildPspEdat(edatDrmType: 2, pgdDrmType: 2, "UP0001-ULUS00002_00-TESTPSP00000002", seed: 9);
        var ex = Assert.Throws<PkgFormatException>(() => PspEdatFile.DecryptToArray(new MemoryStream(edat)));
        Assert.Contains("fuse", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
