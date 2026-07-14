using System;
using System.IO;
using System.Linq;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class ExtractAllTests
{
    [Fact]
    public void ExtractAll_WritesEveryFile_RebuildingTheTree()
    {
        var eboot = new byte[] { 1, 2, 3, 4, 5 };
        var data = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
        byte[] pkg = new SyntheticPkgBuilder()
            .AddDirectory("USRDIR")
            .AddFile("PARAM.SFO", new SfoBuilder().AddString("TITLE", "T").Build())
            .AddFile("USRDIR/EBOOT.BIN", eboot)
            .AddFile("USRDIR/DATA.BIN", data)
            .Build();

        string dir = Path.Combine(Path.GetTempPath(), "pkglens-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new MemoryStream(pkg);
            var info = PkgReader.Read(stream, new InMemoryKeyProvider());
            int n = PkgReader.ExtractAll(stream, info, dir, new InMemoryKeyProvider());

            Assert.Equal(3, n); // three files (dir is not counted)
            Assert.True(File.Exists(Path.Combine(dir, "PARAM.SFO")));
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(dir, "USRDIR", "EBOOT.BIN")));
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(dir, "USRDIR", "DATA.BIN")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExtractAll_RespectsFilter()
    {
        byte[] pkg = new SyntheticPkgBuilder()
            .AddFile("PARAM.SFO", new SfoBuilder().AddString("TITLE", "T").Build())
            .AddFile("USRDIR/EBOOT.BIN", new byte[] { 9 })
            .Build();

        string dir = Path.Combine(Path.GetTempPath(), "pkglens-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new MemoryStream(pkg);
            var info = PkgReader.Read(stream, new InMemoryKeyProvider());
            int n = PkgReader.ExtractAll(stream, info, dir, new InMemoryKeyProvider(),
                filter: e => e.Name.EndsWith(".SFO", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(1, n);
            Assert.True(File.Exists(Path.Combine(dir, "PARAM.SFO")));
            Assert.False(File.Exists(Path.Combine(dir, "USRDIR", "EBOOT.BIN")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
