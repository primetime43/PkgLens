using PkgLens.Core;
using PkgLens.Core.Models;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class HeaderTests
{
    [Fact]
    public void Parse_ReadsCoreFields_FromSyntheticPackage()
    {
        byte[] pkg = new SyntheticPkgBuilder()
            .AddFile("PARAM.SFO", new byte[] { 1, 2, 3, 4 })
            .Build();

        var header = PkgHeader.Parse(pkg);

        Assert.Equal(PkgHeader.Magic, header.RawMagic);
        Assert.Equal(PkgFinalization.Debug, header.Finalization);
        Assert.Equal(PkgPlatform.Ps3, header.Platform);
        Assert.Equal(1u, header.ItemCount);
        Assert.Equal("NPUB30910", header.ContentId.TitleId);
    }

    [Fact]
    public void Parse_RetailFinalizationFlag_IsDetected()
    {
        byte[] pkg = new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
            .AddFile("a.txt", "hi")
            .Build();

        var header = PkgHeader.Parse(pkg);
        Assert.Equal(PkgFinalization.Retail, header.Finalization);
        Assert.True(header.IsRetail);
    }

    [Fact]
    public void Parse_BadMagic_ThrowsPkgFormatException()
    {
        var bytes = new byte[PkgHeader.MinLength];
        bytes[0] = 0xDE; bytes[1] = 0xAD;

        var ex = Assert.Throws<PkgFormatException>(() => PkgHeader.Parse(bytes));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_TooShort_ThrowsPkgFormatException()
    {
        var ex = Assert.Throws<PkgFormatException>(() => PkgHeader.Parse(new byte[0x10]));
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QaDigestAndDataRiv_AreCapturedFromHeader()
    {
        var builder = new SyntheticPkgBuilder();
        byte[] pkg = builder.AddFile("x", "y").Build();

        var header = PkgHeader.Parse(pkg);
        Assert.Equal(builder.QaDigest, header.QaDigest);
        Assert.Equal(builder.DataRiv, header.DataRiv);
    }
}
