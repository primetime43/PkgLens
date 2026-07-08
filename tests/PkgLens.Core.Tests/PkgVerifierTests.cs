using System.Linq;
using PkgLens.Core;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;
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
}
