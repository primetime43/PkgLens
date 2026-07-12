using System;
using System.IO;
using PkgLens.Core;
using PkgLens.Core.Self;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class EbootResignerTests
{
    [Fact]
    public void Resign_PlainElf_ProducesFakeSelf()
    {
        byte[] elf = MinimalElf.Build();

        var result = EbootResigner.Resign(elf);

        Assert.Equal(EbootResignAction.ResignedFromElf, result.Action);
        var info = SelfReader.ParseInfo(new MemoryStream(result.Data));
        Assert.True(info.IsLikelyFakeSigned);          // key revision 0x8000
        Assert.Equal(0x8000, info.KeyRevision);
    }

    [Fact]
    public void Resign_AlreadyFakeSigned_ReturnsUnchanged()
    {
        byte[] fself = SelfBuilder.MakeFakeSelf(MinimalElf.Build());

        var result = EbootResigner.Resign(fself);

        Assert.Equal(EbootResignAction.AlreadyFakeSigned, result.Action);
        Assert.Same(fself, result.Data); // passed through, not rebuilt
    }

    [Fact]
    public void Resign_NonElfNonSelf_Throws()
    {
        var junk = new byte[0x80];
        junk[0] = 0x50; junk[1] = 0x4B; // "PK" — a zip, not an ELF/SELF
        Assert.Throws<PkgFormatException>(() => EbootResigner.Resign(junk));
    }
}
