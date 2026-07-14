using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Sfo;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class SfoTests
{
    [Fact]
    public void Parse_RoundTripsStringsAndInts()
    {
        byte[] blob = new SfoBuilder()
            .AddString("TITLE", "Example Game")
            .AddString("TITLE_ID", "NPUB30910")
            .AddString("CATEGORY", "HG")
            .AddString("APP_VER", "01.02")
            .AddInt("PARENTAL_LEVEL", 5)
            .Build();

        var sfo = SfoParser.Parse(blob);

        Assert.Equal("Example Game", sfo.Title);
        Assert.Equal("NPUB30910", sfo.TitleId);
        Assert.Equal("HG", sfo.Category);
        Assert.Equal("01.02", sfo.AppVersion);
        Assert.Equal(5u, sfo.ParentalLevel);
    }

    [Fact]
    public void Parse_PreservesEntryOrderAndFormats()
    {
        byte[] blob = new SfoBuilder()
            .AddString("TITLE", "T")
            .AddInt("N", 42)
            .Build();

        var sfo = SfoParser.Parse(blob);
        Assert.Equal(2, sfo.Entries.Count);
        Assert.Equal(SfoFormat.Utf8, sfo.Entries[0].Format);
        Assert.Equal(SfoFormat.Int32, sfo.Entries[1].Format);
        Assert.Equal(42u, sfo.Entries[1].IntValue);
    }

    [Fact]
    public void Parse_BadMagic_Throws()
    {
        var ex = Assert.Throws<PkgFormatException>(() => SfoParser.Parse(new byte[0x14]));
        Assert.Contains("PARAM.SFO", ex.Message);
    }

    [Fact]
    public void Parse_Truncated_Throws()
    {
        Assert.Throws<PkgFormatException>(() => SfoParser.Parse(new byte[4]));
    }
}
