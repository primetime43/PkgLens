using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public sealed class PackageRecommendationTests
{
    [Fact]
    public void PspPackage_RecommendsDirectExportFirst()
    {
        var builder = new SyntheticPkgBuilder
        {
            Psp = true,
            PspKeyType = 1,
            Finalization = PkgFinalization.Retail,
        };
        builder.AddFile("USRDIR/CONTENT/EBOOT.PBP", new byte[] { 1, 2, 3 });

        PackageRecommendationSet result = PackageRecommendationEngine.Analyze(Read(builder));

        Assert.Equal("PSP package", result.Classification);
        Assert.Equal(PackageRecommendedAction.ExportPsp, result.Actions[0].Action);
        Assert.True(result.Actions[0].IsPrimary);
        Assert.DoesNotContain(result.Actions, action => action.Action == PackageRecommendedAction.ExportVita);
        Assert.DoesNotContain(result.Actions, action => action.Action == PackageRecommendedAction.ConvertCfw);
    }

    [Fact]
    public void VitaDlc_RecommendsVitaExportAndLicenseAudit()
    {
        var builder = new SyntheticPkgBuilder
        {
            Psp = true,
            PspKeyType = 3,
            Finalization = PkgFinalization.Retail,
        };
        builder.AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.VitaDlc);
        builder.AddMetadataU32((uint)PkgMetadataId.DrmType, (uint)PkgDrmType.Local);
        builder.AddFile("sce_sys/package/body.bin", new byte[] { 4, 5, 6 });

        PackageRecommendationSet result = PackageRecommendationEngine.Analyze(Read(builder));

        Assert.Equal("PSVita dlc package", result.Classification);
        Assert.Equal(PackageRecommendedAction.ExportVita, result.Actions[0].Action);
        Assert.Contains(result.Actions, action => action.Action == PackageRecommendedAction.KeyLicenseAudit);
        Assert.DoesNotContain(result.Actions, action => action.Action == PackageRecommendedAction.ExportPsp);
    }

    [Fact]
    public void Ps3GameWithEboot_RecommendsCfwConversionFirst()
    {
        var builder = new SyntheticPkgBuilder
        {
            Finalization = PkgFinalization.Retail,
            RetailAesKey = BundledKeys.Ps3GpkgAesKey,
        };
        builder.AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.GameExec);
        builder.AddFile("USRDIR/EBOOT.BIN", new byte[] { 1 });

        PackageRecommendationSet result = PackageRecommendationEngine.Analyze(Read(builder));

        Assert.Equal("PS3 game/executable package", result.Classification);
        Assert.Equal(PackageRecommendedAction.ConvertCfw, result.Actions[0].Action);
        Assert.True(result.Actions[0].IsPrimary);
    }

    [Fact]
    public void Ps3GameDataWithoutEboot_RecommendsExtractionFirst()
    {
        var builder = new SyntheticPkgBuilder();
        builder.AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.GameData);
        builder.AddFile("USRDIR/data.bin", new byte[] { 1 });

        PackageRecommendationSet result = PackageRecommendationEngine.Analyze(Read(builder));

        Assert.Equal("PS3 game data or update package", result.Classification);
        Assert.Equal(PackageRecommendedAction.ExtractAll, result.Actions[0].Action);
        Assert.DoesNotContain(result.Actions, action => action.Action == PackageRecommendedAction.ConvertCfw);
    }

    [Fact]
    public void DebugPs3Package_DoesNotRecommendRetailCfwConversion()
    {
        var builder = new SyntheticPkgBuilder();
        builder.AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.GameExec);
        builder.AddFile("USRDIR/EBOOT.BIN", new byte[] { 1 });

        PackageRecommendationSet result = PackageRecommendationEngine.Analyze(Read(builder));

        Assert.Equal(PackageRecommendedAction.ExtractAll, result.Actions[0].Action);
        Assert.DoesNotContain(result.Actions, action => action.Action == PackageRecommendedAction.ConvertCfw);
    }

    [Fact]
    public void UndecryptedPackage_RecommendsAuditInsteadOfInvalidActions()
    {
        var info = new PkgInfo
        {
            Header = new PkgHeader
            {
                Platform = PkgPlatform.PspPsVita,
                RawPlatform = 2,
                Finalization = PkgFinalization.Retail,
                PspKeyType = 7,
            },
            Metadata = new PkgMetadata(Array.Empty<PkgMetadataEntry>()),
            IsDecrypted = false,
            DecryptionNote = "Unsupported PSP/PSVita key type 7.",
        };

        PackageRecommendationSet result = PackageRecommendationEngine.Analyze(info);

        Assert.Equal(PackageRecommendedAction.KeyLicenseAudit, result.Actions[0].Action);
        Assert.Contains(result.Actions, action => action.Action == PackageRecommendedAction.Verify);
        Assert.DoesNotContain(result.Actions, action => action.Action is PackageRecommendedAction.ExportPsp or
            PackageRecommendedAction.ExportVita or PackageRecommendedAction.ConvertCfw or PackageRecommendedAction.ExtractAll);
    }

    private static PkgInfo Read(SyntheticPkgBuilder builder)
    {
        using var package = new MemoryStream(builder.Build());
        return PkgReader.Read(package, new FileKeyProvider());
    }
}
