using System;
using System.IO;
using System.Linq;
using PkgLens.Core;
using PkgLens.Core.Self;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class GameFolderInfoTests
{
    [Fact]
    public void Describe_ReadsSfoAndFlagsFakeSignedEboot()
    {
        using var tmp = new TempDir();
        byte[] sfo = new SfoBuilder()
            .AddString("TITLE", "Test Game")
            .AddString("TITLE_ID", "NPUB30910")
            .AddString("CATEGORY", "HG")
            .AddString("CONTENT_ID", "UP0001-NPUB30910_00-EXAMPLE000000001")
            .Build();
        File.WriteAllBytes(Path.Combine(tmp.Path, "PARAM.SFO"), sfo);

        Directory.CreateDirectory(Path.Combine(tmp.Path, "USRDIR"));
        byte[] fself = SelfBuilder.MakeFakeSelf(MinimalElf.Build());
        File.WriteAllBytes(Path.Combine(tmp.Path, "USRDIR", "EBOOT.BIN"), fself);

        var r = GameFolderInfo.Describe(tmp.Path);

        Assert.Equal("Test Game", r.Title);
        Assert.Equal("NPUB30910", r.TitleId);
        Assert.Equal("UP0001-NPUB30910_00-EXAMPLE000000001", r.ContentId);
        Assert.True(r.HasParamSfo);
        Assert.Equal(2, r.FileCount);            // PARAM.SFO + EBOOT.BIN
        Assert.Equal(1, r.DirectoryCount);       // USRDIR

        var eboot = Assert.Single(r.Eboots);
        Assert.Equal(EbootState.FakeSigned, eboot.State);
        Assert.Equal(0x8000, eboot.KeyRevision);
        Assert.Equal("USRDIR/EBOOT.BIN", eboot.RelativePath);
    }

    [Fact]
    public void Describe_PlainElfEboot_ReportedAsPlainElf()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "EBOOT.BIN"), MinimalElf.Build());

        var r = GameFolderInfo.Describe(tmp.Path);

        Assert.Equal(EbootState.PlainElf, Assert.Single(r.Eboots).State);
        Assert.False(r.HasParamSfo);
    }

    [Fact]
    public void Describe_MissingFolder_Throws() =>
        Assert.Throws<PkgFormatException>(() => GameFolderInfo.Describe(Path.Combine(Path.GetTempPath(), "pkglens-nope-" + Guid.NewGuid())));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pkglens-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
