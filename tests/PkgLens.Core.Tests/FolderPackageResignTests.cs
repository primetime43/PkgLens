using System;
using System.IO;
using System.Linq;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class FolderPackageResignTests
{
    [Fact]
    public void Pack_WithResignEboot_PacksEbootAsFakeSelf()
    {
        using var tmp = new TempDir();
        // A content-id-shaped folder name lets Fast Pack infer the content id with no PARAM.SFO.
        string content = Path.Combine(tmp.Path, "UP0001-NPUB30910_00-EXAMPLE000000001");
        Directory.CreateDirectory(Path.Combine(content, "USRDIR"));
        // A plaintext ELF EBOOT (needs no key/RAP to resign).
        File.WriteAllBytes(Path.Combine(content, "USRDIR", "EBOOT.BIN"), MinimalElf.Build());

        var plan = FolderPackage.Plan(content, new PackOptions
        {
            Finalization = PkgFinalization.Debug,
            ResignEboot = true,
        });
        Assert.Contains(plan.Notes, n => n.Contains("resigned", StringComparison.OrdinalIgnoreCase));

        using var pkg = new MemoryStream();
        plan.Builder.Build(pkg, new InMemoryKeyProvider());
        pkg.Position = 0;

        var info = PkgReader.Read(pkg, new InMemoryKeyProvider());
        var entry = info.Entries.Single(e => e.Name == "USRDIR/EBOOT.BIN");
        byte[] packed = PkgReader.ExtractEntryBytes(pkg, info.Header, entry, new InMemoryKeyProvider());

        // The packed EBOOT is now a fake-signed SELF, not the raw ELF.
        var self = SelfReader.ParseInfo(new MemoryStream(packed));
        Assert.True(self.IsLikelyFakeSigned);
        Assert.Equal(0x8000, self.KeyRevision);
    }

    [Fact]
    public void Pack_WithoutResign_LeavesEbootUntouched()
    {
        using var tmp = new TempDir();
        string content = Path.Combine(tmp.Path, "UP0001-NPUB30910_00-EXAMPLE000000001");
        Directory.CreateDirectory(Path.Combine(content, "USRDIR"));
        byte[] elf = MinimalElf.Build();
        File.WriteAllBytes(Path.Combine(content, "USRDIR", "EBOOT.BIN"), elf);

        var plan = FolderPackage.Plan(content, new PackOptions { Finalization = PkgFinalization.Debug });
        using var pkg = new MemoryStream();
        plan.Builder.Build(pkg, new InMemoryKeyProvider());
        pkg.Position = 0;

        var info = PkgReader.Read(pkg, new InMemoryKeyProvider());
        var entry = info.Entries.Single(e => e.Name == "USRDIR/EBOOT.BIN");
        byte[] packed = PkgReader.ExtractEntryBytes(pkg, info.Header, entry, new InMemoryKeyProvider());

        Assert.Equal(elf, packed); // unchanged
    }

    [Fact]
    public void Pack_WithResignEboot_ResolvesKlicenseeBySelfContentId()
    {
        using var tmp = new TempDir();
        const string contentId = "UP0001-NPUB30910_00-EXAMPLE000000001";
        string content = Path.Combine(tmp.Path, contentId);
        Directory.CreateDirectory(Path.Combine(content, "USRDIR"));
        File.WriteAllBytes(Path.Combine(content, "USRDIR", "EBOOT.BIN"), new SyntheticSelfBuilder
        {
            NpdrmContentId = contentId,
            NpdrmLicenseType = (uint)NpdrmLicenseType.Local,
        }.Build());
        string? resolvedContentId = null;

        PackPlan plan = FolderPackage.Plan(content, new PackOptions
        {
            Finalization = PkgFinalization.Debug,
            ResignEboot = true,
            EbootKlicenseeResolver = id =>
            {
                resolvedContentId = id;
                return new byte[16];
            },
        });

        Assert.Equal(contentId, resolvedContentId);
        Assert.Contains(plan.Notes, note => note.Contains("license resolved from RAP library", StringComparison.Ordinal));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pkglens-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
