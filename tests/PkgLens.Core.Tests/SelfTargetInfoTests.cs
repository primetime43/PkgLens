using System.Buffers.Binary;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public class SelfTargetInfoTests
{
    [Theory]
    [InlineData(4, 0x0A, SelfTargetFormat.CexRetail)]
    [InlineData(8, 0x04, SelfTargetFormat.CexRetail)]
    [InlineData(4, 0x1D, SelfTargetFormat.CexRetail)]
    [InlineData(4, 0x8000, SelfTargetFormat.DexDebug)]
    [InlineData(8, 0xC000, SelfTargetFormat.DexDebug)]
    [InlineData(4, 0x8123, SelfTargetFormat.Unknown)]
    [InlineData(4, 0x1234, SelfTargetFormat.Unknown)]
    [InlineData(2, 0x0A, SelfTargetFormat.Unknown)]
    public void HeaderAndParsedInfoAgree(int type, int revision, SelfTargetFormat expected)
    {
        byte[] bytes = new SyntheticSelfBuilder { ProgramType = (uint)type, KeyRevision = (ushort)revision }.Build();
        var target = SelfTargetInfo.ReadHeader(bytes)!;
        Assert.Equal(expected, target.Format);
        Assert.Equal(target, SelfTargetInfo.FromInfo(SelfReader.ParseInfo(new MemoryStream(bytes))));
    }

    [Fact]
    public void RetailFormatDoesNotClaimValidSignature()
    {
        var target = SelfTargetInfo.ReadHeader(EncryptedSelfBuilder.Build(DevKlicFixture.Elf(1024), new()))!;
        Assert.Equal(SelfTargetFormat.CexRetail, target.Format);
        Assert.Contains("not verified", target.Detail);
    }

    [Fact]
    public void FakeSelfDoesNotClaimDexOnly()
    {
        var target = SelfTargetInfo.ReadHeader(SelfBuilder.MakeFakeSelf(DevKlicFixture.Elf(1024), false))!;
        Assert.Equal(SelfTargetFormat.DexDebug, target.Format);
        Assert.Contains("also run on CEX", target.Detail);
    }

    [Fact]
    public void PlainElfAndOtherFilesAreNotAssumedCex()
    {
        Assert.Equal(SelfTargetInfo.PlainElf, SelfTargetInfo.ReadHeader(DevKlicFixture.Elf(1024)));
        Assert.Null(SelfTargetInfo.ReadHeader("readme text"u8));
        Assert.Null(SelfTargetInfo.ReadHeader(new SyntheticPkgBuilder().Build()));
    }

    [Fact]
    public void TruncatedForeignAndOverflowingHeadersStayUnknown()
    {
        byte[] bytes = new SyntheticSelfBuilder().Build();
        for (int size = 4; size < 0x90; size++)
            Assert.Equal(SelfTargetFormat.Unknown, SelfTargetInfo.ReadHeader(bytes.AsSpan(0, size))!.Format);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0x28), ulong.MaxValue);
        Assert.Equal(SelfTargetFormat.Unknown, SelfTargetInfo.ReadHeader(bytes)!.Format);
        bytes[7] = 3; // A different SCE version must not inherit PS3's revision mapping.
        Assert.Equal(SelfTargetFormat.Unknown, SelfTargetInfo.ReadHeader(bytes)!.Format);
    }
}
