using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PkgLens.Core;
using PkgLens.Core.Psp;
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

    [Fact]
    public void ExportPbp_DirectlyExtractsEbootFromPspPackage()
    {
        byte[] pkg = PspPackage().Build();
        using var source = new MemoryStream(pkg);
        using var destination = new MemoryStream();

        PspExportResult result = PspPackageExporter.Export(source, destination,
            new FileKeyProvider(), PspExportFormat.Pbp);

        Assert.Equal(PspExportFormat.Pbp, result.Format);
        Assert.Equal("USRDIR/CONTENT/EBOOT.PBP", result.PackageEntry);
        Assert.Equal("EBOOT payload bytes", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(destination.Length, result.OutputSize);
    }

    [Fact]
    public void ExportEligibility_AcceptsPspPackageWithEboot()
    {
        using var source = new MemoryStream(PspPackage().Build());

        PspExportEligibility eligibility = PspPackageExporter.CheckEligibility(source, new FileKeyProvider());

        Assert.True(eligibility.CanExport);
        Assert.Equal("USRDIR/CONTENT/EBOOT.PBP", eligibility.PackageEntry);
    }

    [Fact]
    public void ExportEligibility_RejectsPs3PackageEvenWhenItContainsEbootPbp()
    {
        byte[] pkg = new SyntheticPkgBuilder
        {
            Finalization = PkgFinalization.Debug,
            ContentId = "UP0001-BLUS12345_00-EXAMPLEPS3000001",
        }
            .AddFile("USRDIR/CONTENT/EBOOT.PBP", Encoding.UTF8.GetBytes("not PSP content"))
            .Build();
        using var source = new MemoryStream(pkg);

        PspExportEligibility eligibility = PspPackageExporter.CheckEligibility(source, new FileKeyProvider());

        Assert.False(eligibility.CanExport);
        Assert.Contains("PS3", eligibility.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportEligibility_RejectsPspPackageWithoutEbootPbp()
    {
        byte[] pkg = new SyntheticPkgBuilder
        {
            Psp = true,
            Finalization = PkgFinalization.Retail,
            ContentId = "UP0001-ULUS12345_00-EXAMPLEPSP000001",
        }
            .AddFile("PARAM.SFO", Sfo(), pspTypeHigh: 0x00)
            .Build();
        using var source = new MemoryStream(pkg);

        PspExportEligibility eligibility = PspPackageExporter.CheckEligibility(source, new FileKeyProvider());

        Assert.False(eligibility.CanExport);
        Assert.Contains("EBOOT.PBP", eligibility.Reason);
    }

    [Fact]
    public void Repack_PspPackage_PreservesBothKeyDomains()
    {
        byte[] pkg = PspPackage().Build();
        var keys = new FileKeyProvider();
        using var source = new MemoryStream(pkg);
        var info = PkgReader.Read(source, keys);
        var eboot = info.Entries.Single(e => e.Name == "USRDIR/CONTENT/EBOOT.PBP");
        byte[] replacement = Encoding.UTF8.GetBytes("replacement PSP payload");

        using var destination = new MemoryStream();
        PkgWriter.Repack(source, info,
            new Dictionary<PkgEntry, byte[]> { [eboot] = replacement }, keys, destination);

        using var repacked = new MemoryStream(destination.ToArray());
        var repackedInfo = PkgReader.Read(repacked, keys);
        Assert.Equal(info.Entries.Select(e => e.Name), repackedInfo.Entries.Select(e => e.Name));
        Assert.Equal("PSP Test Game", repackedInfo.Sfo?.Title);

        var repackedEboot = repackedInfo.Entries.Single(e => e.Name == "USRDIR/CONTENT/EBOOT.PBP");
        Assert.Equal(replacement,
            PkgReader.ExtractEntryBytes(repacked, repackedInfo.Header, repackedEboot, keys));

        var icon = repackedInfo.Entries.Single(e => e.Name == "ICON0.PNG");
        Assert.Equal("not-really-a-png", Encoding.UTF8.GetString(
            PkgReader.ExtractEntryBytes(repacked, repackedInfo.Header, icon, keys)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Read_VitaPackage_AutoSelectsKeyRevision(byte keyType)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"Vita key revision {keyType}");
        byte[] pkg = new SyntheticPkgBuilder
        {
            Psp = true,
            PspKeyType = keyType,
            Finalization = PkgFinalization.Retail,
            ContentId = "UP0001-PCSE12345_00-EXAMPLEVITA00001",
        }
            .AddFile("sce_sys/param.sfo", Sfo(), pspTypeHigh: 0x00)
            .AddFile("eboot.bin", payload, pspTypeHigh: 0x00)
            .Build();

        using var stream = new MemoryStream(pkg);
        var info = PkgReader.Read(stream, new FileKeyProvider());

        Assert.True(info.IsDecrypted);
        Assert.Equal(keyType, info.Header.PspKeyType);
        Assert.Equal("PSVita", info.Header.PlatformDisplay);
        var entry = info.Entries.Single(item => item.Name == "eboot.bin");
        Assert.Equal(payload, PkgReader.ExtractEntryBytes(stream, info.Header, entry, new FileKeyProvider()));
    }
}
