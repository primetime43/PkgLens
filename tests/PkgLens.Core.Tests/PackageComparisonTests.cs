using System.Text;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Sfo;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public sealed class PackageComparisonTests
{
    [Fact]
    public void Compare_ReportsFileAndSfoChanges()
    {
        using var files = new ComparisonPackages();

        PackageComparisonResult result = PackageComparison.Compare(
            files.BasePath, files.TargetPath, new InMemoryKeyProvider());

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(2, result.ModifiedCount);
        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(PackageFileChange.Modified,
            result.Files.Single(file => file.Path == "USRDIR/CHANGED.BIN").Change);
        Assert.Equal(PackageFileChange.Added,
            result.Files.Single(file => file.Path == "USRDIR/NEW.BIN").Change);
        Assert.Equal(PackageFileChange.Removed,
            result.Files.Single(file => file.Path == "USRDIR/REMOVED.BIN").Change);
        Assert.Equal("Modified", result.SfoValues.Single(value => value.Key == "APP_VER").Change);
        Assert.Equal("01.00", result.Base.Version);
        Assert.Equal("01.01", result.Target.Version);
        Assert.True(result.CanBuildOverlay);
    }

    [Fact]
    public void BuildOverlay_IncludesOnlyChangedAndAddedFiles_WithPatchSfo()
    {
        using var files = new ComparisonPackages();
        var keys = new InMemoryKeyProvider();
        PackageComparisonResult comparison = PackageComparison.Compare(files.BasePath, files.TargetPath, keys);
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pkg");
        try
        {
            PackageOverlayBuildResult build = PackageComparison.BuildOverlay(comparison, outputPath, keys);

            Assert.Equal(3, build.IncludedFileCount);
            Assert.Equal(1, build.OmittedRemovalCount);
            using var output = File.OpenRead(outputPath);
            var package = PkgReader.Read(output, keys);
            string[] fileNames = package.Entries.Where(entry => entry.IsFile).Select(entry => entry.Name).ToArray();
            Assert.Contains("PARAM.SFO", fileNames);
            Assert.Contains("USRDIR/CHANGED.BIN", fileNames);
            Assert.Contains("USRDIR/NEW.BIN", fileNames);
            Assert.DoesNotContain("USRDIR/SAME.BIN", fileNames);
            Assert.DoesNotContain("USRDIR/REMOVED.BIN", fileNames);
            Assert.Equal("GP", package.Sfo?.Category);
            Assert.Equal("01.01", package.Sfo?.AppVersion);
            Assert.Equal((uint)PkgLens.Core.Shared.Models.PkgContentType.GameData,
                package.Metadata.ContentTypeRaw);

            var changed = package.Entries.Single(entry => entry.Name == "USRDIR/CHANGED.BIN");
            Assert.Equal(Encoding.ASCII.GetBytes("target changed payload"),
                PkgReader.ExtractEntryBytes(output, package.Header, changed, keys));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Compare_WarnsWhenTargetIsOlderOrDifferentTitle()
    {
        string basePath = WritePackage("BLUS00001", "02.00", "Base");
        string targetPath = WritePackage("BLES00002", "01.00", "Target");
        try
        {
            PackageComparisonResult result = PackageComparison.Compare(basePath, targetPath, new InMemoryKeyProvider());
            Assert.Contains(result.Warnings, warning => warning.Contains("Title IDs differ", StringComparison.Ordinal));
            Assert.Contains(result.Warnings, warning => warning.Contains("appears older", StringComparison.Ordinal));
            Assert.False(result.CanBuildOverlay);
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(targetPath);
        }
    }

    private static string WritePackage(string titleId, string version, string payload)
    {
        byte[] sfo = BuildSfo(titleId, version);
        byte[] package = new SyntheticPkgBuilder
        {
            ContentId = $"UP0001-{titleId}_00-COMPARETEST00001",
        }
            .AddFile("PARAM.SFO", sfo)
            .AddFile("USRDIR/DATA.BIN", Encoding.ASCII.GetBytes(payload))
            .Build();
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pkg");
        File.WriteAllBytes(path, package);
        return path;
    }

    private static byte[] BuildSfo(string titleId, string version) => new SfoBuilder()
        .AddString("TITLE", "Comparison Test")
        .AddString("TITLE_ID", titleId)
        .AddString("CATEGORY", "HG")
        .AddString("APP_VER", version)
        .Build();

    private sealed class ComparisonPackages : IDisposable
    {
        public ComparisonPackages()
        {
            BasePath = BuildBase();
            TargetPath = BuildTarget();
        }

        public string BasePath { get; }
        public string TargetPath { get; }

        private static string BuildBase()
        {
            byte[] package = new SyntheticPkgBuilder
            {
                ContentId = "UP0001-BLUS12345_00-COMPARETEST00001",
            }
                .AddFile("PARAM.SFO", BuildSfo("BLUS12345", "01.00"))
                .AddFile("USRDIR/SAME.BIN", Encoding.ASCII.GetBytes("same payload"))
                .AddFile("USRDIR/CHANGED.BIN", Encoding.ASCII.GetBytes("base payload"))
                .AddFile("USRDIR/REMOVED.BIN", Encoding.ASCII.GetBytes("removed payload"))
                .Build();
            return Write(package);
        }

        private static string BuildTarget()
        {
            byte[] package = new SyntheticPkgBuilder
            {
                ContentId = "UP0001-BLUS12345_00-COMPARETEST00001",
            }
                .AddFile("PARAM.SFO", BuildSfo("BLUS12345", "01.01"))
                .AddFile("USRDIR/SAME.BIN", Encoding.ASCII.GetBytes("same payload"))
                .AddFile("USRDIR/CHANGED.BIN", Encoding.ASCII.GetBytes("target changed payload"))
                .AddFile("USRDIR/NEW.BIN", Encoding.ASCII.GetBytes("new payload"))
                .Build();
            return Write(package);
        }

        private static string Write(byte[] package)
        {
            string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pkg");
            File.WriteAllBytes(path, package);
            return path;
        }

        public void Dispose()
        {
            File.Delete(BasePath);
            File.Delete(TargetPath);
        }
    }
}
