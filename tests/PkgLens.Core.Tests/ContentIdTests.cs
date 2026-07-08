using PkgLens.Core.Models;
using Xunit;

namespace PkgLens.Core.Tests;

public class ContentIdTests
{
    [Fact]
    public void Parse_DecomposesCanonicalContentId()
    {
        var id = ContentId.Parse("UP0001-NPUB30910_00-EXAMPLE000000001");

        Assert.Equal("UP0001", id.Region);
        Assert.Equal("NPUB30910", id.TitleId);
        Assert.Equal("00", id.Variant);
        Assert.Equal("EXAMPLE000000001", id.Name);
    }

    [Fact]
    public void Parse_TrimsTrailingNullsAndPreservesRaw()
    {
        var id = ContentId.Parse("UP0001-NPUB30910_00-EXAMPLE000000001\0\0\0");
        Assert.Equal("UP0001-NPUB30910_00-EXAMPLE000000001", id.Raw);
    }

    [Fact]
    public void Parse_EmptyString_YieldsNullFields()
    {
        var id = ContentId.Parse("");
        Assert.Equal(string.Empty, id.Raw);
        Assert.Null(id.Region);
        Assert.Null(id.TitleId);
    }

    [Fact]
    public void Parse_MissingUnderscore_LeavesVariantNull()
    {
        var id = ContentId.Parse("UP0001-NPUB30910-EXAMPLE000000001");
        Assert.Equal("NPUB30910", id.TitleId);
        Assert.Null(id.Variant);
    }
}
