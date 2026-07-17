using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Tests;

public sealed class PackageLibraryMatcherTests
{
    [Fact]
    public void Analyze_GroupsCompleteBaseUpdateAndDlcSet()
    {
        PackageScanRow[] packages =
        {
            Row("base.pkg", "UP0001-NPUB12345_00-BASE000000000001", "NPUB12345", "Game", "01.00", "HG", PackageLibraryRole.Base),
            Row("update.pkg", "UP0001-NPUB12345_00-PATCH00000000001", "NPUB12345", "Game Update", "01.10", "GP", PackageLibraryRole.Update),
            Row("dlc.pkg", "UP0001-NPUB12345_00-DLC0000000000001", "NPUB12345", "Game DLC", "01.00", "AC", PackageLibraryRole.Dlc),
        };

        PackageLibraryReport report = PackageLibraryMatcher.Analyze(packages);

        PackageLibraryGroup group = Assert.Single(report.Groups);
        Assert.True(group.HasBase);
        Assert.Equal(1, group.UpdateCount);
        Assert.Equal(1, group.DlcCount);
        Assert.Empty(report.Warnings);
        Assert.Equal(new[] { PackageLibraryRole.Base, PackageLibraryRole.Update, PackageLibraryRole.Dlc },
            report.Packages.Select(package => package.Role));
    }

    [Fact]
    public void Analyze_WarnsWhenUpdateHasNoBase()
    {
        PackageScanRow update = Row("update.pkg", "EP0001-NPEB12345_00-PATCH00000000001",
            "NPEB12345", "Example", "02.00", "GP", PackageLibraryRole.Update);

        PackageLibraryReport report = PackageLibraryMatcher.Analyze(new[] { update });

        PackageLibraryWarning warning = Assert.Single(report.Warnings);
        Assert.Equal(PackageLibraryWarningKind.MissingBase, warning.Kind);
        Assert.Contains("no matching base", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(update.File, warning.Files);
    }

    [Fact]
    public void Analyze_WarnsAboutContentAndTitleRegionMismatch()
    {
        PackageScanRow package = Row("mismatch.pkg", "EP0001-NPUB12345_00-PATCH00000000001",
            "NPUB12345", "Example", "01.01", "GP", PackageLibraryRole.Update);

        PackageLibraryReport report = PackageLibraryMatcher.Analyze(new[] { package });

        Assert.Contains(report.Warnings, warning => warning.Kind == PackageLibraryWarningKind.RegionMismatch &&
            warning.Message.Contains("conflicting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_SuggestsLikelyBaseFromAnotherRegion()
    {
        PackageScanRow europeanBase = Row("eu-base.pkg", "EP0001-NPEB12345_00-BASE000000000001",
            "NPEB12345", "Same Game", "01.00", "HG", PackageLibraryRole.Base);
        PackageScanRow usUpdate = Row("us-update.pkg", "UP0001-NPUB54321_00-PATCH00000000001",
            "NPUB54321", "Same Game Update", "01.10", "GP", PackageLibraryRole.Update);

        PackageLibraryReport report = PackageLibraryMatcher.Analyze(new[] { europeanBase, usUpdate });

        Assert.Contains(report.Warnings, warning => warning.Kind == PackageLibraryWarningKind.RegionMismatch &&
            warning.Message.Contains("may belong", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PkgContentType.GameExec, "HG", PackageLibraryRole.Base)]
    [InlineData(PkgContentType.GameData, "GP", PackageLibraryRole.Update)]
    [InlineData(PkgContentType.VitaDlc, "ac", PackageLibraryRole.Dlc)]
    [InlineData(PkgContentType.VitaTheme, "th", PackageLibraryRole.Theme)]
    public void Classify_UsesContentTypeAndCategory(
        PkgContentType contentType, string category, PackageLibraryRole expected) =>
        Assert.Equal(expected, PackageLibraryMatcher.Classify(contentType, category));

    private static PackageScanRow Row(string file, string contentId, string titleId, string title,
        string version, string category, PackageLibraryRole role) =>
        new(file, contentId, "PS3", "Retail", titleId, title, version, 100, true, null,
            PackageLibraryMatcher.ResolveRegion(contentId, titleId), category, null, role);
}
