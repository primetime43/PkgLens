using System.Linq;
using System.Text;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class PkgBuilderTests
{
    private static byte[] Sfo(string title = "Packed Game", string titleId = "NPUB30910",
        string? contentId = null)
    {
        var b = new SfoBuilder()
            .AddString("TITLE", title)
            .AddString("TITLE_ID", titleId)
            .AddString("CATEGORY", "HG");
        if (contentId is not null) b.AddString("CONTENT_ID", contentId);
        return b.Build();
    }

    [Fact]
    public void Build_Debug_RoundTripsEntriesAndSfo()
    {
        var eboot = Enumerable.Range(0, 5000).Select(i => (byte)(i * 3 + 7)).ToArray();
        var builder = new PkgBuilder
        {
            ContentId = "UP0001-NPUB30910_00-EXAMPLE000000001",
            InstallDirectory = "NPUB30910",
        };
        builder.AddFile("PARAM.SFO", Sfo())
               .AddDirectory("USRDIR")
               .AddFile("USRDIR/EBOOT.BIN", eboot, PkgEntryType.Npdrm)
               .AddFile("USRDIR/DATA.BIN", new byte[] { 1, 2, 3 });

        using var pkg = new MemoryStream();
        builder.Build(pkg, new InMemoryKeyProvider());
        pkg.Position = 0;

        var info = PkgReader.Read(pkg, new InMemoryKeyProvider());
        Assert.True(info.IsDecrypted);
        Assert.Equal(PkgFinalization.Debug, info.Header.Finalization);
        Assert.Equal("UP0001-NPUB30910_00-EXAMPLE000000001", info.ContentId.Raw);
        Assert.Equal("NPUB30910", info.ContentId.TitleId);
        Assert.Equal("Packed Game", info.Sfo?.Title);

        var names = info.Entries.Select(e => e.Name).ToHashSet();
        Assert.Contains("PARAM.SFO", names);
        Assert.Contains("USRDIR", names);
        Assert.Contains("USRDIR/EBOOT.BIN", names);

        var ebootEntry = info.Entries.Single(e => e.Name == "USRDIR/EBOOT.BIN");
        Assert.Equal(PkgEntryType.Npdrm, ebootEntry.Kind);
        Assert.Equal(eboot, PkgReader.ExtractEntryBytes(pkg, info.Header, ebootEntry, new InMemoryKeyProvider()));
    }

    [Fact]
    public void Build_Debug_WritesInspectableMetadata()
    {
        var builder = new PkgBuilder
        {
            ContentId = "UP0001-NPUB30910_00-EXAMPLE000000001",
            InstallDirectory = "NPUB30910",
            ContentType = (uint)PkgContentType.GameExec,
            DrmType = 3,
        };
        builder.AddFile("PARAM.SFO", Sfo());

        using var pkg = new MemoryStream();
        builder.Build(pkg, new InMemoryKeyProvider());
        pkg.Position = 0;

        var info = PkgReader.Read(pkg, new InMemoryKeyProvider());
        Assert.Equal(3u, info.Metadata.DrmType);
        Assert.Equal(PkgContentType.GameExec, info.Metadata.ContentType);
        Assert.Equal("NPUB30910", info.Metadata.InstallDirectory);
    }

    [Fact]
    public void Build_Debug_PassesVerification()
    {
        var builder = new PkgBuilder { ContentId = "UP0001-NPUB30910_00-EXAMPLE000000001" };
        builder.AddFile("PARAM.SFO", Sfo());

        using var pkg = new MemoryStream();
        builder.Build(pkg, new InMemoryKeyProvider());
        pkg.Position = 0;

        var report = PkgVerifier.Verify(pkg, new InMemoryKeyProvider());
        Assert.True(report.Passed, string.Join("; ",
            report.Checks.Where(c => c.Status == PkgCheckStatus.Fail).Select(c => c.Name + ": " + c.Detail)));
    }

    [Fact]
    public void Build_Retail_RoundTripsWithKey()
    {
        byte[] key = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var payload = Enumerable.Range(0, 3000).Select(i => (byte)(i * 5 + 1)).ToArray();

        var builder = new PkgBuilder
        {
            Finalization = PkgFinalization.Retail,
            ContentId = "UP0001-NPUB30910_00-EXAMPLE000000001",
        };
        builder.AddFile("PARAM.SFO", Sfo()).AddFile("A.BIN", payload);

        using var pkg = new MemoryStream();
        builder.Build(pkg, new InMemoryKeyProvider(key));
        pkg.Position = 0;

        var info = PkgReader.Read(pkg, new InMemoryKeyProvider(key));
        Assert.Equal(PkgFinalization.Retail, info.Header.Finalization);
        var a = info.Entries.Single(e => e.Name == "A.BIN");
        Assert.Equal(payload, PkgReader.ExtractEntryBytes(pkg, info.Header, a, new InMemoryKeyProvider(key)));

        // The CMAC we wrote (public gpkg-key integrity MAC) verifies with the same key.
        pkg.Position = 0;
        var report = PkgVerifier.Verify(pkg, new InMemoryKeyProvider(key));
        Assert.Equal(PkgCheckStatus.Pass, report.Checks.Single(c => c.Name == "Header CMAC").Status);
    }

    [Fact]
    public void Build_ThenExtract_ReproducesFolderTree()
    {
        var builder = new PkgBuilder { ContentId = "UP0001-NPUB30910_00-EXAMPLE000000001" };
        builder.AddFile("PARAM.SFO", Sfo())
               .AddDirectory("USRDIR")
               .AddFile("USRDIR/EBOOT.BIN", Encoding.ASCII.GetBytes("boot"), PkgEntryType.Npdrm);

        using var pkg = new MemoryStream();
        builder.Build(pkg, new InMemoryKeyProvider());
        pkg.Position = 0;

        var info = PkgReader.Read(pkg, new InMemoryKeyProvider());
        string outDir = Path.Combine(Path.GetTempPath(), "pkglens_build_" + Guid.NewGuid().ToString("N"));
        try
        {
            int n = PkgReader.ExtractAll(pkg, info, outDir, new InMemoryKeyProvider());
            Assert.Equal(2, n); // PARAM.SFO + EBOOT.BIN
            Assert.Equal("boot", File.ReadAllText(Path.Combine(outDir, "USRDIR", "EBOOT.BIN")));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void Plan_FastPack_InfersContentIdFromSfo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pkglens_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "USRDIR"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "PARAM.SFO"),
                Sfo(titleId: "NPUB30910", contentId: "UP0001-NPUB30910_00-EXAMPLE000000001"));
            File.WriteAllText(Path.Combine(dir, "USRDIR", "EBOOT.BIN"), "boot");

            PackPlan plan = FolderPackage.Plan(dir);
            Assert.Equal("UP0001-NPUB30910_00-EXAMPLE000000001", plan.ContentId);
            Assert.Equal("NPUB30910", plan.InstallDirectory);
            Assert.Equal(2, plan.FileCount);      // PARAM.SFO + EBOOT.BIN
            Assert.Equal(1, plan.DirectoryCount); // USRDIR
            Assert.Equal((uint)PkgContentType.GameExec, plan.ContentType);

            using var pkg = new MemoryStream();
            plan.Builder.Build(pkg, new InMemoryKeyProvider());
            pkg.Position = 0;
            var info = PkgReader.Read(pkg, new InMemoryKeyProvider());
            Assert.Contains(info.Entries, e => e.Name == "USRDIR/EBOOT.BIN" && e.Kind == PkgEntryType.Npdrm);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Plan_CustomPack_OverridesInference()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pkglens_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "DATA.BIN"), "x");
            var plan = FolderPackage.Plan(dir, new PackOptions
            {
                ContentId = "EP9000-NPEB90210_00-CUSTOM0000000001",
                InstallDirectory = "MYDIR",
                ContentType = (uint)PkgContentType.Theme,
                DrmType = 1,
            });
            Assert.Equal("EP9000-NPEB90210_00-CUSTOM0000000001", plan.ContentId);
            Assert.Equal("MYDIR", plan.InstallDirectory);
            Assert.Equal((uint)PkgContentType.Theme, plan.ContentType);
            Assert.Equal(1u, plan.DrmType);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Plan_NoContentId_Throws()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pkglens_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "DATA.BIN"), "x"); // no PARAM.SFO, non-content-id folder name
            Assert.Throws<PkgFormatException>(() => FolderPackage.Plan(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
