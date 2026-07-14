using PkgLens.Core.Shared.Models;
using Xunit;

namespace PkgLens.Core.Tests;

public class PkgMetadataTests
{
    private static PkgMetadata One(uint id, byte[] data) =>
        new(new[] { new PkgMetadataEntry { RawId = id, Data = data } });

    [Fact]
    public void Id0x08_IsSoftwareRevision_NotSystemVersion()
    {
        // The label comes from the enum name; 0x08 must read as "SoftwareRevision".
        var e = new PkgMetadataEntry { RawId = 0x08, Data = new byte[8] };
        Assert.Equal(PkgMetadataId.SoftwareRevision, e.Id);
        Assert.Equal("SoftwareRevision", e.Label);
    }

    [Fact]
    public void SoftwareRevision_DecodesFirmwareVersionAppParts()
    {
        // unk=00, firmware=04 75 00, version=01 00, app=02 03
        var md = One(0x08, new byte[] { 0x00, 0x04, 0x75, 0x00, 0x01, 0x00, 0x02, 0x03 });
        Assert.Equal("firmware 04.7500, version 01.00, app 02.03", md.SoftwareRevisionText);
    }

    [Fact]
    public void SoftwareRevision_NullWhenAbsentOrTooShort()
    {
        Assert.Null(One(0x08, new byte[4]).SoftwareRevisionText); // truncated
        Assert.Null(new PkgMetadata(System.Array.Empty<PkgMetadataEntry>()).SoftwareRevisionText);
    }

    [Fact]
    public void AsUInt32_IsExactlyFourBytes()
    {
        Assert.Equal(0x01020304u, new PkgMetadataEntry { RawId = 1, Data = new byte[] { 1, 2, 3, 4 } }.AsUInt32());
        // An 8-byte field is not a u32 — the old ">= 4" logic wrongly read its high word.
        Assert.Null(new PkgMetadataEntry { RawId = 4, Data = new byte[8] }.AsUInt32());
    }

    [Fact]
    public void PackageSize_ReadsFullU64_NotHighWord()
    {
        // 8-byte package size 0x0000000012345678 — the old u32 read returned 0 (the high word).
        var e = new PkgMetadataEntry { RawId = 0x04, Data = new byte[] { 0, 0, 0, 0, 0x12, 0x34, 0x56, 0x78 } };
        Assert.Null(e.AsUInt32());
        Assert.Equal(0x12345678UL, e.AsUInt64());
    }
}
