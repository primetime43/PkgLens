using System.Text.Json;
using System.Text.Json.Nodes;
using PkgLens.Core;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public class BatchProcessorTests
{
    [Fact]
    public void Classify_PersistsManifest_AndSkipsCompletedJobsOnResume()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        string output = root.CreateDirectory("output");
        File.WriteAllBytes(Path.Combine(source, "base.pkg"),
            new SyntheticPkgBuilder().AddFile("DATA.BIN", "base").Build());
        File.WriteAllBytes(Path.Combine(nested, "update.pkg"),
            new SyntheticPkgBuilder().AddFile("DATA.BIN", "update").Build());

        BatchManifest manifest = BatchProcessor.Create(source, output, BatchOperation.Classify,
            recursive: true, new InMemoryKeyProvider());
        Assert.Equal(2, manifest.Jobs.Count);
        Assert.True(File.Exists(manifest.ManifestPath));

        BatchProcessor.Run(manifest, new InMemoryKeyProvider());
        Assert.All(manifest.Jobs, job => Assert.Equal(BatchJobStatus.Completed, job.Status));
        Assert.All(manifest.Jobs, job => Assert.True(File.Exists(job.OutputPath)));
        Assert.True(File.Exists(Path.Combine(output, "classify", "library-report.json")));

        BatchManifest resumed = BatchProcessor.Load(manifest.ManifestPath);
        BatchProcessor.Run(resumed, new InMemoryKeyProvider());
        Assert.All(resumed.Jobs, job => Assert.Equal(1, job.Attempts));
    }

    [Fact]
    public void Verify_ContinuesAfterFailure_AndSavesBothReports()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string output = root.CreateDirectory("output");
        byte[] good = new SyntheticPkgBuilder().AddFile("DATA.BIN", new byte[32]).Build();
        byte[] bad = good.ToArray();
        bad[0x50] ^= 0x80;
        File.WriteAllBytes(Path.Combine(source, "a-good.pkg"), good);
        File.WriteAllBytes(Path.Combine(source, "b-bad.pkg"), bad);

        BatchManifest manifest = BatchProcessor.Create(source, output, BatchOperation.Verify,
            recursive: false, new InMemoryKeyProvider());
        BatchProcessor.Run(manifest, new InMemoryKeyProvider());

        Assert.Equal(BatchJobStatus.Completed,
            manifest.Jobs.Single(job => job.RelativePath == "a-good.pkg").Status);
        BatchJob failed = manifest.Jobs.Single(job => job.RelativePath == "b-bad.pkg");
        Assert.Equal(BatchJobStatus.Failed, failed.Status);
        Assert.Contains("verification", failed.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.All(manifest.Jobs, job => Assert.True(File.Exists(job.OutputPath)));
    }

    [Fact]
    public void Extract_CancellationLeavesPendingJob_ThenResumeCompletes()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string output = root.CreateDirectory("output");
        byte[] package = new SyntheticPkgBuilder()
            .AddFile("USRDIR/A.BIN", new byte[1024 * 1024])
            .AddFile("USRDIR/B.BIN", new byte[1024 * 1024])
            .Build();
        File.WriteAllBytes(Path.Combine(source, "game.pkg"), package);
        BatchManifest manifest = BatchProcessor.Create(source, output, BatchOperation.Extract,
            recursive: false, new InMemoryKeyProvider());
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<BatchRunProgress>(value =>
        {
            if (value.ItemPercent > 0) cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() => BatchProcessor.Run(manifest,
            new InMemoryKeyProvider(), cancellationToken: cancellation.Token, progress: progress));
        BatchJob job = Assert.Single(manifest.Jobs);
        Assert.Equal(BatchJobStatus.Pending, job.Status);
        Assert.False(Directory.Exists(job.OutputPath + ".partial"));

        BatchManifest resumed = BatchProcessor.Load(manifest.ManifestPath);
        BatchProcessor.Run(resumed, new InMemoryKeyProvider());
        BatchJob completed = Assert.Single(resumed.Jobs);
        Assert.Equal(BatchJobStatus.Completed, completed.Status);
        Assert.Equal(2, completed.Attempts);
        Assert.True(File.Exists(Path.Combine(completed.OutputPath, "USRDIR", "A.BIN")));
        Assert.True(File.Exists(Path.Combine(completed.OutputPath, "USRDIR", "B.BIN")));
    }

    [Fact]
    public void ConvertCfw_ConvertsPs3AndSkipsPspPackage()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string output = root.CreateDirectory("output");
        byte[] fakeSelf = SelfBuilder.MakeFakeSelf(MinimalElf.Build());
        File.WriteAllBytes(Path.Combine(source, "ps3.pkg"),
            new SyntheticPkgBuilder
            {
                Finalization = PkgLens.Core.Shared.Models.PkgFinalization.Retail,
                RetailAesKey = BundledKeys.Ps3GpkgAesKey,
            }.AddFile("USRDIR/EBOOT.BIN", fakeSelf).Build());
        File.WriteAllBytes(Path.Combine(source, "psp.pkg"),
            new SyntheticPkgBuilder { Psp = true, Finalization = PkgLens.Core.Shared.Models.PkgFinalization.Retail }
                .AddFile("USRDIR/EBOOT.PBP", new byte[64], pspTypeHigh: 0x90).Build());

        BatchManifest manifest = BatchProcessor.Create(source, output, BatchOperation.ConvertCfw,
            recursive: false, new FileKeyProvider(), TargetCompatibilityProfile.Rpcs3);
        Assert.Equal(TargetCompatibilityProfile.Rpcs3, manifest.TargetProfile);
        Assert.Contains("\"targetProfile\": \"rpcs3\"", File.ReadAllText(manifest.ManifestPath));

        BatchManifest resumed = BatchProcessor.Load(manifest.ManifestPath);
        Assert.Equal(TargetCompatibilityProfile.Rpcs3, resumed.TargetProfile);
        BatchProcessor.Run(resumed, new FileKeyProvider());

        BatchJob ps3 = resumed.Jobs.Single(job => job.RelativePath == "ps3.pkg");
        BatchJob psp = resumed.Jobs.Single(job => job.RelativePath == "psp.pkg");
        Assert.Equal(BatchJobStatus.Completed, ps3.Status);
        Assert.True(File.Exists(ps3.OutputPath));
        Assert.True(File.Exists(Path.ChangeExtension(ps3.OutputPath, ".report.txt")));
        Assert.Contains("Target profile : RPCS3 emulator",
            File.ReadAllText(Path.ChangeExtension(ps3.OutputPath, ".report.txt")));
        Assert.Equal(BatchJobStatus.Skipped, psp.Status);
        Assert.Contains("not a PS3", psp.Message!);
    }

    [Fact]
    public void Load_LegacyManifestWithoutTargetProfile_DefaultsToCexCfw()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string output = root.CreateDirectory("output");
        File.WriteAllBytes(Path.Combine(source, "game.pkg"),
            new SyntheticPkgBuilder().AddFile("DATA.BIN", "x").Build());
        BatchManifest manifest = BatchProcessor.Create(source, output, BatchOperation.ConvertCfw,
            recursive: false, new InMemoryKeyProvider());
        JsonObject json = JsonNode.Parse(File.ReadAllText(manifest.ManifestPath))!.AsObject();
        Assert.True(json.Remove("targetProfile"));
        File.WriteAllText(manifest.ManifestPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        BatchManifest legacy = BatchProcessor.Load(manifest.ManifestPath);

        Assert.Equal(TargetCompatibilityProfile.CexCfw, legacy.TargetProfile);
    }

    [Fact]
    public void Load_RejectsManifestWhoseOutputWasRedirectedOutsideBatchFolder()
    {
        using var root = new TempDirectory();
        string source = root.CreateDirectory("source");
        string output = root.CreateDirectory("output");
        File.WriteAllBytes(Path.Combine(source, "game.pkg"),
            new SyntheticPkgBuilder().AddFile("DATA.BIN", "x").Build());
        BatchManifest manifest = BatchProcessor.Create(source, output, BatchOperation.Classify,
            recursive: false, new InMemoryKeyProvider());

        string json = File.ReadAllText(manifest.ManifestPath);
        using JsonDocument document = JsonDocument.Parse(json);
        string original = document.RootElement.GetProperty("jobs")[0].GetProperty("outputPath").GetString()!;
        string redirected = Path.Combine(root.Path, "outside.json");
        File.WriteAllText(manifest.ManifestPath, json.Replace(
            JsonEncodedText.Encode(original).ToString(), JsonEncodedText.Encode(redirected).ToString()));

        Assert.Throws<PkgFormatException>(() => BatchProcessor.Load(manifest.ManifestPath));
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pkglens-batch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string name) => Directory.CreateDirectory(
            System.IO.Path.Combine(Path, name)).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
