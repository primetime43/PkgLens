using System.IO;
using PkgLens.Core;
using PkgLens.Core.Self;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class SelfReaderTests
{
    [Fact]
    public void Parse_Npdrm_ReadsHeaderAndControlInfo()
    {
        byte[] self = new SyntheticSelfBuilder().Build();

        var info = SelfReader.ParseInfo(new MemoryStream(self));

        Assert.Equal(SceCategory.Self, info.Category);
        Assert.Equal(SelfProgramType.Npdrm, info.ProgramType);
        Assert.True(info.IsNpdrm);
        Assert.Equal(0x0004, info.KeyRevision);
        Assert.Equal(0x11668u, info.DataLength);
        Assert.Equal(0x1010000001000003u, info.AuthId);

        Assert.NotNull(info.Elf);
        Assert.True(info.Elf!.Is64Bit);
        Assert.True(info.Elf.IsBigEndian);
        Assert.Equal(2, info.Elf.Type);       // EXEC
        Assert.Equal(0x15, info.Elf.Machine); // PPC64

        Assert.NotNull(info.Npdrm);
        Assert.Equal("UP0001-NPUB30910_00-EXAMPLE000000001", info.Npdrm!.ContentId);
        Assert.Equal(NpdrmLicenseType.Free, info.Npdrm.LicenseType);
    }

    [Fact]
    public void Parse_NonNpdrmApp_HasNoNpdrmBlock()
    {
        byte[] self = new SyntheticSelfBuilder
        {
            ProgramType = 4,          // Application
            NpdrmContentId = null,    // no NPDRM control block
        }.Build();

        var info = SelfReader.ParseInfo(new MemoryStream(self));
        Assert.Equal(SelfProgramType.Application, info.ProgramType);
        Assert.False(info.IsNpdrm);
        Assert.Null(info.Npdrm);
    }

    [Fact]
    public void Parse_FakeSigned_IsFlagged()
    {
        byte[] self = new SyntheticSelfBuilder { KeyRevision = 0x8000 }.Build();
        var info = SelfReader.ParseInfo(new MemoryStream(self));
        Assert.True(info.IsLikelyFakeSigned);
    }

    [Fact]
    public void Parse_ReadsControlBlocksAndControlFlags()
    {
        byte[] flags = Enumerable.Range(0, 0x20).Select(i => (byte)(0xC0 + i)).ToArray();
        byte[] self = new SyntheticSelfBuilder { ControlFlags = flags }.Build();

        var info = SelfReader.ParseInfo(new MemoryStream(self));

        Assert.NotNull(info.ControlFlags);
        Assert.Equal(flags, info.ControlFlags);
        // Both the control-flags (type 1) and the NPDRM (type 3) blocks should be listed, in order.
        Assert.Collection(info.ControlBlocks,
            b => Assert.Equal(1u, b.RawType),
            b => Assert.Equal(3u, b.RawType));
    }

    [Fact]
    public void Parse_RealFakeSelf_ReadsSegmentTable()
    {
        // Build a real fSELF from a two-segment ELF and read its plaintext segment table back.
        byte[] elf = MinimalElf.Build(
            (1u, new byte[16]),   // PT_LOAD, 16 bytes
            (1u, new byte[32]));  // PT_LOAD, 32 bytes
        byte[] fself = SelfBuilder.MakeFakeSelf(elf);

        var info = SelfReader.ParseInfo(new MemoryStream(fself));

        Assert.Equal(2, info.Segments.Count);
        Assert.Equal(16u, (uint)info.Segments[0].Size);
        Assert.Equal(32u, (uint)info.Segments[1].Size);
        Assert.All(info.Segments, s => Assert.False(s.Compressed));   // fSELF stores segments uncompressed
        // A fake-signed SELF carries the type-2 "file/ELF digest" control block.
        Assert.Contains(info.ControlBlocks, b => b.RawType == 2);
    }

    [Fact]
    public void Parse_NotASelf_Throws()
    {
        var notSelf = new byte[0x100];
        notSelf[0] = 0x7F; notSelf[1] = (byte)'E'; notSelf[2] = (byte)'L'; notSelf[3] = (byte)'F';
        Assert.Throws<PkgFormatException>(() => SelfReader.ParseInfo(new MemoryStream(notSelf)));
    }

    [Fact]
    public void IsSelf_DetectsMagic()
    {
        byte[] self = new SyntheticSelfBuilder().Build();
        Assert.True(SelfReader.IsSelf(self));
        Assert.False(SelfReader.IsSelf(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }));
    }
}
