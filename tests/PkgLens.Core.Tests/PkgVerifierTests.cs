using System.Linq;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class PkgVerifierTests
{
    private static byte[] Sfo() => new SfoBuilder().AddString("TITLE", "V").AddString("TITLE_ID", "NPUB30910").Build();

    private static PkgCheck Check(PkgVerificationReport r, string name) =>
        r.Checks.Single(c => c.Name == name);

    [Fact]
    public void Verify_CleanDebugPackage_Passes()
    {
        byte[] pkg = new SyntheticPkgBuilder()
            .AddFile("PARAM.SFO", Sfo())
            .AddFile("USRDIR/DATA.BIN", new byte[100])
            .Build();

        using var s = new MemoryStream(pkg);
        var report = PkgVerifier.Verify(s, new InMemoryKeyProvider());

        Assert.True(report.Passed);
        Assert.Equal(PkgCheckStatus.Pass, Check(report, "Header SHA-1").Status);
        Assert.Equal(PkgCheckStatus.Skipped, Check(report, "Header CMAC").Status); // debug: no CMAC
        Assert.Equal(PkgCheckStatus.Skipped, Check(report, "Header ECDSA").Status); // synthetic: no signature
        Assert.Equal(PkgCheckStatus.Pass, Check(report, "Item table").Status);
    }

    [Fact]
    public void Verify_CleanRetailPackage_WithKey_CmacPasses()
    {
        var builder = new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
            .AddFile("PARAM.SFO", Sfo());
        byte[] pkg = builder.Build();

        using var s = new MemoryStream(pkg);
        var report = PkgVerifier.Verify(s, new InMemoryKeyProvider(builder.RetailAesKey));

        Assert.True(report.Passed);
        Assert.Equal(PkgCheckStatus.Pass, Check(report, "Header SHA-1").Status);
        Assert.Equal(PkgCheckStatus.Pass, Check(report, "Header CMAC").Status);
    }

    [Fact]
    public void Verify_TamperedHeader_FailsSha1()
    {
        byte[] pkg = new SyntheticPkgBuilder().AddFile("PARAM.SFO", Sfo()).Build();
        pkg[0x50] ^= 0xFF; // flip a byte inside header[0x00:0x80] (content-id padding area)

        using var s = new MemoryStream(pkg);
        var report = PkgVerifier.Verify(s, new InMemoryKeyProvider());

        Assert.False(report.Passed);
        Assert.Equal(PkgCheckStatus.Fail, Check(report, "Header SHA-1").Status);
    }

    [Fact]
    public void Verify_RetailWrongKey_FailsCmac()
    {
        var builder = new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
            .AddFile("PARAM.SFO", Sfo());
        byte[] pkg = builder.Build();

        using var s = new MemoryStream(pkg);
        var report = PkgVerifier.Verify(s, new InMemoryKeyProvider(new byte[16])); // all-zero wrong key

        Assert.Equal(PkgCheckStatus.Fail, Check(report, "Header CMAC").Status);
    }

    [Fact]
    public void Verify_Truncated_FailsTotalSize()
    {
        byte[] pkg = new SyntheticPkgBuilder().AddFile("PARAM.SFO", Sfo()).Build();
        byte[] truncated = pkg.Take(pkg.Length - 64).ToArray();

        using var s = new MemoryStream(truncated);
        var report = PkgVerifier.Verify(s, new InMemoryKeyProvider());

        Assert.False(report.Passed);
        Assert.Equal(PkgCheckStatus.Fail, Check(report, "Total size").Status);
    }

    [Fact]
    public void Verify_HugeDataOffset_FailsDataRegion_NotFooledByOverflow()
    {
        // data_offset with the high bit set casts to a negative long; the old signed comparison
        // reported "Pass" for this garbage. It must Fail on the data-region bounds check.
        byte[] header = BuildBareHeader(dataOffset: 0x8000000000000000UL, dataSize: 0x10);

        using var s = new MemoryStream(header);
        var report = PkgVerifier.Verify(s, new InMemoryKeyProvider());

        Assert.Equal(PkgCheckStatus.Fail, Check(report, "Data region").Status);
    }

    /// <summary>A minimal 0xC0-byte debug PKG header (valid magic, no real body) for bounds-check tests.</summary>
    private static byte[] BuildBareHeader(ulong dataOffset, ulong dataSize)
    {
        var h = new byte[0xC0];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x00), PkgHeader.Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(0x04), 0x0000); // debug
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(0x06), 0x0001); // PS3
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(0x18), (ulong)h.Length); // total_size
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(0x20), dataOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(0x28), dataSize);
        return h;
    }
}
