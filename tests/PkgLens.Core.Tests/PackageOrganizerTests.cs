using System.Security.Cryptography;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public class PackageOrganizerTests
{
    [Fact]
    public void Analyze_DetectsExactDuplicatesContentVariantsAndSupersededUpdates()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        string output = Path.Combine(source, "Organized Packages");

        byte[] basePackage = BuildPackage("UP0001-NPUB12345_00-BASE000000000001",
            "NPUB12345", "Example Game", "01.00", "HG", "base");
        File.WriteAllBytes(Path.Combine(source, "base.pkg"), basePackage);
        File.WriteAllBytes(Path.Combine(nested, "base-copy.pkg"), basePackage);
        File.WriteAllBytes(Path.Combine(source, "update-110.pkg"), BuildPackage(
            "UP0001-NPUB12345_00-PATCH00000000001", "NPUB12345", "Example Game", "01.10", "GP", "old"));
        File.WriteAllBytes(Path.Combine(source, "update-120.pkg"), BuildPackage(
            "UP0001-NPUB12345_00-PATCH00000000001", "NPUB12345", "Example Game", "01.20", "GP", "new"));

        PackageOrganizerPlan plan = PackageOrganizer.Analyze(source, output, recursive: true,
            new InMemoryKeyProvider());

        Assert.Equal(4, plan.Items.Count);
        PackageOrganizerItem duplicate = plan.Items.Single(item => item.RelativePath.EndsWith("base-copy.pkg"));
        Assert.True(duplicate.IsExactDuplicate);
        Assert.Equal(PackageOrganizerAction.SkipExactDuplicate, duplicate.Action);
        Assert.Null(duplicate.DestinationPath);
        Assert.Contains(plan.Items, item => item.IsContentIdDuplicate && item.Package.Version == "01.20");
        PackageOrganizerItem superseded = plan.Items.Single(item => item.Package.Version == "01.10");
        Assert.True(superseded.IsSupersededUpdate);
        Assert.Equal("01.20", superseded.SupersededByVersion);
        Assert.Contains("Superseded", superseded.Relationship);
        Assert.Contains(Path.Combine("Updates", "Superseded"), superseded.DestinationPath!);
        Assert.All(plan.Items.Where(item => item.DestinationPath is not null), item =>
            Assert.Contains(Path.Combine("PS3", "NPUB12345 - Example Game"), item.DestinationPath!));
    }

    [Fact]
    public void Analyze_ContentIdVariantsWithSameNameGetCollisionSafeHashSuffix()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string first = Directory.CreateDirectory(Path.Combine(source, "one")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(source, "two")).FullName;
        string output = root.CreateDirectory("output");
        const string contentId = "UP0001-NPUB54321_00-VARIANT000000001";
        File.WriteAllBytes(Path.Combine(first, "game.pkg"),
            BuildPackage(contentId, "NPUB54321", "Variant Game", "01.00", "HG", "first"));
        File.WriteAllBytes(Path.Combine(second, "game.pkg"),
            BuildPackage(contentId, "NPUB54321", "Variant Game", "01.00", "HG", "second"));

        PackageOrganizerPlan plan = PackageOrganizer.Analyze(source, output, recursive: true,
            new InMemoryKeyProvider());
        string[] destinations = plan.Items.Select(item => item.DestinationPath!).ToArray();

        Assert.Equal(2, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(destinations, path => Path.GetFileName(path).Contains('[', StringComparison.Ordinal));
        Assert.Equal(1, plan.ContentIdDuplicateCount);
    }

    [Fact]
    public void Apply_CopyBuildsPlatformTitleRoleHierarchyAndVerifiesHash()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string output = root.CreateDirectory("output");
        string input = Path.Combine(source, "base.pkg");
        File.WriteAllBytes(input, BuildPackage("UP0001-NPUB77777_00-BASE000000000001",
            "NPUB77777", "Copy Game", "01.00", "HG", "payload"));
        PackageOrganizerPlan plan = PackageOrganizer.Analyze(source, output, recursive: false,
            new InMemoryKeyProvider());

        PackageOrganizer.Apply(plan, PackageOrganizerMode.Copy);

        PackageOrganizerItem item = Assert.Single(plan.Items);
        Assert.Equal(PackageOrganizerStatus.Copied, item.Status);
        Assert.True(File.Exists(input));
        Assert.True(File.Exists(item.DestinationPath));
        Assert.Equal(item.Sha256, Hash(item.DestinationPath!));
        Assert.Contains(Path.Combine("PS3", "NPUB77777 - Copy Game", "Base"), item.DestinationPath!);
        Assert.True(File.Exists(Path.Combine(output, "package-organizer-report.json")));
    }

    [Fact]
    public void Apply_MoveDeletesOnlyVerifiedCanonicalAndLeavesExactDuplicateUntouched()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string output = root.CreateDirectory("output");
        byte[] package = BuildPackage("UP0001-NPUB88888_00-BASE000000000001",
            "NPUB88888", "Move Game", "01.00", "HG", "payload");
        string canonical = Path.Combine(source, "a.pkg");
        string duplicate = Path.Combine(source, "b.pkg");
        File.WriteAllBytes(canonical, package);
        File.WriteAllBytes(duplicate, package);
        PackageOrganizerPlan plan = PackageOrganizer.Analyze(source, output, recursive: false,
            new InMemoryKeyProvider());

        PackageOrganizer.Apply(plan, PackageOrganizerMode.Move);

        PackageOrganizerItem moved = plan.Items.Single(item => item.Action == PackageOrganizerAction.Organize);
        PackageOrganizerItem retained = plan.Items.Single(item => item.IsExactDuplicate);
        Assert.Equal(PackageOrganizerStatus.Moved, moved.Status);
        Assert.False(File.Exists(moved.SourcePath));
        Assert.True(File.Exists(moved.DestinationPath));
        Assert.Equal(PackageOrganizerStatus.Skipped, retained.Status);
        Assert.True(File.Exists(retained.SourcePath));
    }

    private static byte[] BuildPackage(string contentId, string titleId, string title,
        string version, string category, string payload)
    {
        byte[] sfo = new SfoBuilder()
            .AddString("TITLE", title)
            .AddString("TITLE_ID", titleId)
            .AddString("APP_VER", version)
            .AddString("CATEGORY", category)
            .Build();
        uint contentType = category == "GP" ? (uint)PkgContentType.GameData : (uint)PkgContentType.GameExec;
        return new SyntheticPkgBuilder { ContentId = contentId }
            .AddMetadataU32((uint)PkgMetadataId.ContentType, contentType)
            .AddFile("PARAM.SFO", sfo)
            .AddFile("USRDIR/DATA.BIN", payload)
            .Build();
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pkglens-organizer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string CreateDirectory(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
