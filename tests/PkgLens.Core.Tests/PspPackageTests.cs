using System.IO;
using System.Linq;
using System.Text;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// Exercises PSP/PSX package support: platform 0x0002, key_type 1, and the per-entry key selection
/// where type-0x90 entries use the PSP key and all others use the PS3 gpkg key. Verified end-to-end
/// against a real PSP minis package (GTA: Liberty City Stories) during development; here with a
/// synthetic fixture built from the bundled public keys (no copyrighted data).
/// </summary>
public class PspPackageTests
{
    private static byte[] Sfo() =>
        new SfoBuilder().AddString("TITLE", "PSP Test Game").AddString("TITLE_ID", "ULUS12345").Build();

    private static SyntheticPkgBuilder PspPackage()
    {
        // Mix both key domains: root files (PARAM.SFO, ICON0.PNG) use the gpkg key (type high 0x00),
        // USRDIR content uses the PSP key (type high 0x90) — exactly like a real PSP minis package.
        return new SyntheticPkgBuilder
        {
            Psp = true,
            Finalization = PkgFinalization.Retail,
            ContentId = "UP0001-ULUS12345_00-EXAMPLEPSP000001",
        }
            .AddFile("PARAM.SFO", Sfo(), pspTypeHigh: 0x00)
            .AddFile("ICON0.PNG", Encoding.UTF8.GetBytes("not-really-a-png"), pspTypeHigh: 0x00)
            .AddDirectory("USRDIR", pspTypeHigh: 0x00)
            .AddDirectory("USRDIR/CONTENT", pspTypeHigh: 0x90)
            .AddFile("USRDIR/CONTENT/EBOOT.PBP", Encoding.UTF8.GetBytes("EBOOT payload bytes"), pspTypeHigh: 0x90);
    }

    [Fact]
    public void Read_PspPackage_DecodesPlatformAndKeyType()
    {
        byte[] pkg = PspPackage().Build();
        using var s = new MemoryStream(pkg);

        var info = PkgReader.Read(s, new FileKeyProvider());

        Assert.Equal(PkgPlatform.PspPsVita, info.Header.Platform);
        Assert.Equal(1, info.Header.PspKeyType);
        Assert.True(info.IsDecrypted);
    }

    [Fact]
    public void Read_PspPackage_DecryptsBothKeyDomains()
    {
        byte[] pkg = PspPackage().Build();
        using var s = new MemoryStream(pkg);

        var info = PkgReader.Read(s, new FileKeyProvider());

        // Names from both the gpkg-keyed (root) and PSP-keyed (USRDIR) entries must all decode.
        var names = info.Entries.Select(e => e.Name).ToArray();
        Assert.Contains("PARAM.SFO", names);
        Assert.Contains("ICON0.PNG", names);
        Assert.Contains("USRDIR/CONTENT/EBOOT.PBP", names);

        // PARAM.SFO (gpkg-keyed) must have parsed.
        Assert.NotNull(info.Sfo);
        Assert.Equal("PSP Test Game", info.Sfo!.Title);
        Assert.Equal("ULUS12345", info.Sfo.TitleId);
    }

    [Fact]
    public void Read_PspPackage_FlagsPspEntries()
    {
        byte[] pkg = PspPackage().Build();
        using var s = new MemoryStream(pkg);
        var info = PkgReader.Read(s, new FileKeyProvider());

        // type-0x90 entries (0x80 overwrite | 0x10 PSP) carry the PSP flag; gpkg-keyed (0x00) don't.
        Assert.True(info.Entries.Single(e => e.Name == "USRDIR/CONTENT/EBOOT.PBP").IsPsp);
        Assert.False(info.Entries.Single(e => e.Name == "PARAM.SFO").IsPsp);
        Assert.False(info.Entries.Single(e => e.Name == "ICON0.PNG").IsPsp);
    }

    [Fact]
    public void Extract_PspPackage_RecoversContentForBothKeys()
    {
        byte[] pkg = PspPackage().Build();
        using var s = new MemoryStream(pkg);
        var info = PkgReader.Read(s, new FileKeyProvider());

        // A PSP-keyed (0x90) file.
        var eboot = info.Entries.Single(e => e.Name == "USRDIR/CONTENT/EBOOT.PBP");
        using var ms = new MemoryStream();
        PkgReader.ExtractEntry(s, info.Header, eboot, ms, new FileKeyProvider());
        Assert.Equal("EBOOT payload bytes", Encoding.UTF8.GetString(ms.ToArray()));

        // A gpkg-keyed (0x00) file.
        var icon = info.Entries.Single(e => e.Name == "ICON0.PNG");
        byte[] iconBytes = PkgReader.ExtractEntryBytes(s, info.Header, icon, new FileKeyProvider());
        Assert.Equal("not-really-a-png", Encoding.UTF8.GetString(iconBytes));
    }
}
