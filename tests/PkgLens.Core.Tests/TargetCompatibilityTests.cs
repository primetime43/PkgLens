using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public sealed class TargetCompatibilityTests
{
    [Theory]
    [InlineData(TargetCompatibilityProfile.Rpcs3, true)]
    [InlineData(TargetCompatibilityProfile.CexCfw, true)]
    [InlineData(TargetCompatibilityProfile.Dex, false)]
    [InlineData(TargetCompatibilityProfile.Hen, true)]
    public void RetailGame_ReportsExpectedTargetCompatibility(
        TargetCompatibilityProfile profile, bool expectedCompatible)
    {
        using var directory = new TempDirectory();
        string packagePath = WritePackage(directory, PkgFinalization.Retail,
            new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
                .AddFile("USRDIR/EBOOT.BIN", MinimalElf.Build()));

        TargetCompatibilityReport report = TargetCompatibilityAnalyzer.AnalyzeCfwConversion(
            packagePath, new FileKeyProvider(), directory.Path, profile);

        Assert.Equal(expectedCompatible, report.IsCompatible);
        Assert.Equal(profile, report.Target.Profile);
        Assert.Contains(report.Checks, check => check.Name == "Package platform" &&
            check.Status == TargetCompatibilityStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "RAP / licenses" &&
            check.Status == TargetCompatibilityStatus.Pass);
    }

    [Theory]
    [InlineData(TargetCompatibilityProfile.Rpcs3, true)]
    [InlineData(TargetCompatibilityProfile.CexCfw, false)]
    [InlineData(TargetCompatibilityProfile.Hen, false)]
    public void DebugPackage_IsAcceptedOnlyByEmulatorWorkflow(
        TargetCompatibilityProfile profile, bool expectedCompatible)
    {
        using var directory = new TempDirectory();
        string packagePath = WritePackage(directory, PkgFinalization.Debug,
            new SyntheticPkgBuilder().AddFile("USRDIR/EBOOT.BIN", MinimalElf.Build()));

        TargetCompatibilityReport report = TargetCompatibilityAnalyzer.AnalyzeCfwConversion(
            packagePath, new FileKeyProvider(), directory.Path, profile);

        Assert.Equal(expectedCompatible, report.IsCompatible);
        Assert.Contains(report.Checks, check => check.Name == "Package layout" &&
            check.Status == (expectedCompatible
                ? TargetCompatibilityStatus.Pass
                : TargetCompatibilityStatus.Error));
    }

    [Fact]
    public void Hen_SystemContent_IsBlocked()
    {
        using var directory = new TempDirectory();
        var builder = new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
            .AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.Vsh)
            .AddFile("dev_flash/vsh/module/test.sprx", MinimalElf.Build());
        string packagePath = WritePackage(directory, PkgFinalization.Retail, builder);

        TargetCompatibilityReport report = TargetCompatibilityAnalyzer.AnalyzeCfwConversion(
            packagePath, new FileKeyProvider(), directory.Path, TargetCompatibilityProfile.Hen);

        Assert.False(report.IsCompatible);
        Assert.Contains(report.Checks, check => check.Name == "Content scope" &&
            check.Status == TargetCompatibilityStatus.Error);
    }

    [Fact]
    public void Converter_DexTargetRejectsBeforeWriting()
    {
        byte[] package = new SyntheticPkgBuilder()
            .AddFile("USRDIR/EBOOT.BIN", MinimalElf.Build())
            .Build();
        using var source = new MemoryStream(package);
        using var destination = new MemoryStream();

        PkgFormatException exception = Assert.Throws<PkgFormatException>(() =>
            CfwPackageConverter.Convert(source, destination, new FileKeyProvider(),
                new CfwConversionOptions { TargetProfile = TargetCompatibilityProfile.Dex }));

        Assert.Contains("DEX output is not supported", exception.Message);
        Assert.Equal(0, destination.Length);
    }

    private static string WritePackage(TempDirectory directory, PkgFinalization finalization,
        SyntheticPkgBuilder builder)
    {
        if (finalization == PkgFinalization.Retail)
            builder.RetailAesKey = BundledKeys.Ps3GpkgAesKey;
        string path = Path.Combine(directory.Path, $"{finalization}.pkg");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"pkglens-compatibility-tests-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
