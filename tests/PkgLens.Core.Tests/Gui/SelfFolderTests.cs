using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public class SelfFolderTests
{
    [Theory]
    [InlineData(SelfFolderOperation.Rebuild)]
    [InlineData(SelfFolderOperation.FakeSign)]
    [InlineData(SelfFolderOperation.LegacySign)]
    internal void MixedExecutablesPreserveMetadataAndVerify(SelfFolderOperation operation)
    {
        using var f = new FolderFixture();
        byte[] elf = DevKlicFixture.Elf(2048);
        byte[] sprx = (byte[])elf.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(sprx.AsSpan(0x10), 0xFFA4);
        var metadata = new SelfBuilder.FakeSelfOptions
        {
            Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 3,
            NpAppType = 0, AuthId = 0x1010000001000007, VendorId = 0xABCD,
            AppVersion = 0x0003005500000000, FirmwareVersion = 35500,
            ControlFlags = Enumerable.Repeat((byte)1, 32).ToArray(),
        };
        f.Add("EBOOT.BIN", EncryptedSelfBuilder.Build(elf, new()));
        byte[] npOriginal = EncryptedSelfBuilder.Build(sprx, new() { Metadata = metadata, FileName = "module.sprx" });
        f.Add("modules/module.sprx", npOriginal);
        f.Add("plain.elf", elf);
        var plan = f.Scan(operation);
        Assert.Equal(3, plan.Jobs.Count);
        SelfFolderService.Run(plan, new(), f.Raps);
        Assert.All(plan.Jobs, j => Assert.Equal(SelfFolderStatus.Complete, j.Status));
        byte[] np = File.ReadAllBytes(Path.Combine(f.Output, "modules/module.sprx"));
        Assert.Equal(sprx, SelfDecryptor.Decrypt(np).Elf);
        Assert.Equal(npOriginal, File.ReadAllBytes(Path.Combine(f.Source, "modules/module.sprx")));
        var info = SelfReader.ParseInfo(new MemoryStream(np));
        Assert.Equal(metadata.AuthId, info.AuthId);
        Assert.Equal(metadata.VendorId, info.VendorId);
        Assert.Equal(metadata.AppVersion, info.SdkVersion);
        Assert.Equal(metadata.FirmwareVersion, info.FirmwareVersion);
        Assert.Equal(metadata.ControlFlags, info.ControlFlags);
        Assert.Equal(3u, info.Npdrm!.RawLicenseType);
        Assert.Equal(0u, info.Npdrm.AppType);
        Assert.Equal(DevKlicFixture.ContentId, info.Npdrm.ContentId);
        Assert.Equal(elf, SelfDecryptor.Decrypt(File.ReadAllBytes(Path.Combine(f.Output, "plain.elf.self"))).Elf);
        if (operation == SelfFolderOperation.LegacySign)
            Assert.All(plan.Jobs, j => Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(File.ReadAllBytes(j.Output))));
        else if (operation == SelfFolderOperation.FakeSign)
            Assert.Equal(0x8000, info.KeyRevision);
    }

    [Fact]
    public void DifferentLicensedFilesResolveTheirOwnRaps()
    {
        using var f = new FolderFixture();
        byte[] elf = DevKlicFixture.Elf(2048);
        var keys = new List<byte[]>();
        for (int i = 1; i <= 2; i++)
        {
            string id = $"UP0001-NPUB5432{i}_00-BATCHLICENSE0001";
            byte[] rap = Enumerable.Repeat((byte)i, 16).ToArray();
            byte[] key = NpdKeys.RapToKlicensee(rap); keys.Add(key);
            f.Add($"game{i}/EBOOT.BIN", EncryptedSelfBuilder.Build(elf, new()
            {
                Metadata = new() { Npdrm = true, ContentId = id, NpLicenseType = 2 }, Klicensee = key,
            }));
            File.WriteAllBytes(Path.Combine(f.Raps, id + ".rap"), rap);
        }
        var plan = f.Scan(SelfFolderOperation.LegacySign);
        SelfFolderService.Run(plan, new(), f.Raps);
        for (int i = 0; i < 2; i++)
        {
            var job = plan.Jobs[i];
            Assert.Equal(SelfFolderStatus.Complete, job.Status);
            byte[] bytes = File.ReadAllBytes(job.Output);
            Assert.Equal(elf, SelfDecryptor.Decrypt(bytes, keys[i]).Elf);
            Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(bytes, keys[i]));
            Assert.Equal(2u, SelfReader.ParseInfo(new MemoryStream(bytes)).Npdrm!.RawLicenseType);
        }
        var decrypt = SelfFolderService.Scan(f.Output, Path.Combine(f.Root, "decrypted"), new(SelfFolderOperation.Decrypt));
        SelfFolderService.Run(decrypt, new(), f.Raps);
        Assert.All(decrypt.Jobs, j =>
        {
            Assert.Equal(SelfFolderStatus.Complete, j.Status);
            Assert.Equal(elf, File.ReadAllBytes(j.Output));
        });
    }

    [Fact]
    public void MissingKeyCanBeFixedAndRetriedWithoutRebuildingCompletedFiles()
    {
        using var f = new FolderFixture();
        byte[] elf = DevKlicFixture.Elf(1024);
        byte[] rap = Enumerable.Repeat((byte)41, 16).ToArray();
        byte[] key = NpdKeys.RapToKlicensee(rap);
        f.Add("a.self", EncryptedSelfBuilder.Build(elf, new()
        { Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 2 }, Klicensee = key }));
        f.Add("b.self", SelfBuilder.MakeFakeSelf(elf, false));
        var plan = f.Scan(SelfFolderOperation.Rebuild);
        SelfFolderService.Run(plan, new(), f.Raps);
        Assert.Equal(SelfFolderStatus.Failed, plan.Jobs[0].Status);
        Assert.Equal(SelfFolderStatus.Complete, plan.Jobs[1].Status);
        byte[] completed = File.ReadAllBytes(plan.Jobs[1].Output);
        Assert.False(File.Exists(plan.Jobs[0].Output));
        File.WriteAllBytes(Path.Combine(f.Raps, DevKlicFixture.ContentId + ".rap"), rap);
        SelfFolderService.Run(plan, new(), f.Raps, retryFailed: true);
        Assert.All(plan.Jobs, j => Assert.Equal(SelfFolderStatus.Complete, j.Status));
        Assert.Equal(2, plan.Jobs[0].Attempts);
        Assert.Equal(1, plan.Jobs[1].Attempts);
        Assert.Equal(completed, File.ReadAllBytes(plan.Jobs[1].Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAndResumePreserveCompletedOutputs(bool cancelDuringFile)
    {
        using var f = new FolderFixture();
        f.Add("a.self", SelfBuilder.MakeFakeSelf(DevKlicFixture.Elf(1024), false));
        f.Add("b.self", SelfBuilder.MakeFakeSelf(DevKlicFixture.Elf(1024), false));
        var plan = f.Scan(SelfFolderOperation.Rebuild);
        using var cancellation = new CancellationTokenSource();
        SelfFolderService.Run(plan, new(), f.Raps, token: cancellation.Token,
            progress: new ImmediateProgress(p =>
            {
                if (p.Job.Status == (cancelDuringFile ? SelfFolderStatus.Running : SelfFolderStatus.Complete)) cancellation.Cancel();
            }));
        Assert.Equal(cancelDuringFile ? SelfFolderStatus.Cancelled : SelfFolderStatus.Complete, plan.Jobs[0].Status);
        Assert.Equal(SelfFolderStatus.Pending, plan.Jobs[1].Status);
        Assert.False(File.Exists(plan.Jobs[1].Output));
        if (cancelDuringFile) Assert.False(File.Exists(plan.Jobs[0].Output));
        SelfFolderService.Run(plan, new(), f.Raps);
        Assert.All(plan.Jobs, j => Assert.Equal(SelfFolderStatus.Complete, j.Status));
        Assert.Equal(cancelDuringFile ? 2 : 1, plan.Jobs[0].Attempts);
        Assert.Empty(Directory.EnumerateFiles(f.Output, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void DecryptNamesAreDistinctAndPlainElfsAreSkipped()
    {
        using var f = new FolderFixture();
        byte[] elf = DevKlicFixture.Elf(1024);
        f.Add("same.self", SelfBuilder.MakeFakeSelf(elf, false));
        f.Add("same.sprx", SelfBuilder.MakeFakeSelf(elf, false));
        f.Add("same.elf", elf);
        var plan = f.Scan(SelfFolderOperation.Decrypt);
        SelfFolderService.Run(plan, new(), f.Raps);
        Assert.Equal(2, plan.Jobs.Count(j => j.Status == SelfFolderStatus.Complete));
        Assert.Equal(1, plan.Jobs.Count(j => j.Status == SelfFolderStatus.Skipped));
        Assert.Equal(elf, File.ReadAllBytes(Path.Combine(f.Output, "same.self.elf")));
        Assert.Equal(elf, File.ReadAllBytes(Path.Combine(f.Output, "same.sprx.elf")));
    }

    [Fact]
    public void MalformedFilesDoNotPreventOtherFilesFromCompleting()
    {
        using var f = new FolderFixture();
        f.Add("broken.self", "broken"u8.ToArray());
        f.Add("valid.self", SelfBuilder.MakeFakeSelf(DevKlicFixture.Elf(1024), false));
        var plan = f.Scan(SelfFolderOperation.Decrypt);
        Assert.Equal(SelfFolderStatus.Failed, plan.Jobs[0].Status);
        SelfFolderService.Run(plan, new(), f.Raps);
        SelfFolderService.Run(plan, new(), f.Raps, retryFailed: true);
        Assert.Equal(SelfFolderStatus.Failed, plan.Jobs[0].Status);
        Assert.Equal(SelfFolderStatus.Complete, plan.Jobs[1].Status);
        Assert.False(File.Exists(plan.Jobs[0].Output));
    }

    [Fact]
    public void ExistingOutputAndOutputCreatedAfterScanAreNeverOverwritten()
    {
        using var f = new FolderFixture();
        f.Add("keep.self", SelfBuilder.MakeFakeSelf(DevKlicFixture.Elf(1024), false));
        var plan = f.Scan(SelfFolderOperation.Rebuild);
        Directory.CreateDirectory(f.Output);
        File.WriteAllText(plan.Jobs[0].Output, "keep me");
        SelfFolderService.Run(plan, new(), f.Raps);
        Assert.Equal(SelfFolderStatus.Skipped, plan.Jobs[0].Status);
        Assert.Equal("keep me", File.ReadAllText(plan.Jobs[0].Output));
        Assert.Equal(SelfFolderStatus.Skipped, f.Scan(SelfFolderOperation.Rebuild).Jobs[0].Status);
        string race = Path.Combine(f.Output, "race.self");
        Assert.Throws<IOException>(() => AtomicOutput.Write(race, stream =>
        {
            stream.Write("new"u8);
            File.WriteAllText(race, "another writer");
        }, overwrite: false));
        Assert.Equal("another writer", File.ReadAllText(race));
        Assert.Empty(Directory.EnumerateFiles(f.Output, "*.tmp"));
    }

    [Fact]
    public void RejectsOverlappingRootsAndOutputNameCollisions()
    {
        using var f = new FolderFixture();
        var options = new SelfFolderOptions(SelfFolderOperation.Rebuild);
        Assert.Throws<IOException>(() => SelfFolderService.Scan(f.Source, f.Source, options));
        Assert.Throws<IOException>(() => SelfFolderService.Scan(f.Source, Path.Combine(f.Source, "output"), options));
        Assert.Throws<IOException>(() => SelfFolderService.Scan(f.Source, f.Root, options));
        byte[] elf = DevKlicFixture.Elf(1024);
        f.Add("game.elf", elf);
        f.Add("game.elf.self", SelfBuilder.MakeFakeSelf(elf, false));
        var plan = f.Scan(SelfFolderOperation.Rebuild);
        Assert.All(plan.Jobs, j => Assert.Equal(SelfFolderStatus.Failed, j.Status));
        SelfFolderService.Run(plan, new(), f.Raps, retryFailed: true);
        Assert.All(plan.Jobs, j => Assert.Equal(SelfFolderStatus.Failed, j.Status));
        Assert.False(Directory.Exists(f.Output));
    }

    [Fact]
    public void UnsupportedLegacyProfileDoesNotPublishAndScanHonorsRecursion()
    {
        using var f = new FolderFixture();
        byte[] elf = DevKlicFixture.Elf(1024);
        f.Add("EBOOT.BIN", EncryptedSelfBuilder.Build(elf, new() { Metadata = new()
        { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 3 } }));
        f.Add("sub/second.self", SelfBuilder.MakeFakeSelf(elf, false));
        f.Add("ignore.txt", elf);
        var plan = SelfFolderService.Scan(f.Source, f.Output, new(SelfFolderOperation.LegacySign, Revision: 0, Recursive: false));
        var job = Assert.Single(plan.Jobs);
        Assert.Equal(SelfFolderStatus.Failed, job.Status);
        Assert.Contains("No legacy signing profile", job.Message);
        SelfFolderService.Run(plan, new(), f.Raps, retryFailed: true);
        Assert.False(File.Exists(job.Output));
        Assert.Equal(2, f.Scan(SelfFolderOperation.Rebuild).Jobs.Count);
    }

    [AvaloniaFact]
    public async Task DialogScansRunsAndInvalidatesThePreviewWhenSettingsChange()
    {
        using var f = new FolderFixture();
        f.Add("EBOOT.BIN", SelfBuilder.MakeFakeSelf(DevKlicFixture.Elf(1024), false));
        var dialog = new SelfFolderDialog(f.Raps);
        dialog.Show();
        dialog.FindControl<TextBox>("SourceBox")!.Text = f.Source;
        dialog.FindControl<TextBox>("OutputBox")!.Text = f.Output;
        // TextChanged notifications can be deferred until the dispatcher runs.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await dialog.ScanAsync();
        Assert.True(dialog.FindControl<Button>("RunButton")!.IsEnabled);
        await dialog.RunAsync(false);
        Assert.True(File.Exists(Path.Combine(f.Output, "EBOOT.BIN")));
        Assert.False(dialog.FindControl<Button>("RunButton")!.IsEnabled);
        Assert.Contains("1 complete", dialog.FindControl<TextBlock>("StatusText")!.Text);
        dialog.FindControl<ComboBox>("OperationBox")!.SelectedIndex = 0;
        Assert.False(dialog.FindControl<Control>("RevisionPanel")!.IsVisible);
        Assert.False(dialog.FindControl<Button>("RunButton")!.IsEnabled);
        Assert.Empty(dialog.FindControl<DataGrid>("JobsGrid")!.ItemsSource.Cast<object>());
        dialog.Close();
    }

    private sealed class ImmediateProgress(Action<SelfFolderProgress> callback) : IProgress<SelfFolderProgress>
    { public void Report(SelfFolderProgress value) => callback(value); }

    private sealed class FolderFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pkglens-self-folder-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "input");
        public string Output => Path.Combine(Root, "output");
        public string Raps => Path.Combine(Root, "raps");
        public FolderFixture() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Raps); }
        public void Add(string relative, byte[] bytes)
        {
            string path = Path.Combine(Source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        public SelfFolderPlan Scan(SelfFolderOperation operation) => SelfFolderService.Scan(Source, Output, new(operation));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
