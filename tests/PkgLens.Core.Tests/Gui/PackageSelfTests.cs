using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Shared.Sfo;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public class PackageSelfTests
{
    [Theory]
    [InlineData(SelfFolderOperation.Rebuild)]
    [InlineData(SelfFolderOperation.FakeSign)]
    [InlineData(SelfFolderOperation.LegacySign)]
    internal void FolderPackEmbedsVerifiedExecutablesWithOriginalNames(SelfFolderOperation operation)
    {
        using var f = new Fixture();
        byte[] elf = DevKlicFixture.Elf(2048);
        byte[] npdrm = EncryptedSelfBuilder.Build(elf, new()
        { Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 3 }, FileName = "module.sprx" });
        f.Add("USRDIR/EBOOT.BIN", elf);
        f.Add("USRDIR/module.sprx", npdrm);
        f.Add("DATA.BIN", "unchanged"u8.ToArray());
        using var prepared = PreparedPackageExecutables.ForFolder(f.Source, new(operation));
        Assert.True(prepared.CanBuild);
        Assert.Equal(2, prepared.Checks.Count);
        prepared.ValidateFolder(f.Source, default);
        string destination = Path.Combine(f.Root, "built.pkg");
        BuildFolder(f.Source, destination, prepared);
        using var packed = PackageOperationService.Open(destination, new InMemoryKeyProvider());
        foreach (var entry in packed.Info.Entries.Where(e => e.IsFile && PreparedPackageExecutables.IsExecutable(e.Name)))
        {
            byte[] bytes = packed.ReadEntryBytes(entry);
            Assert.Equal(elf, SelfDecryptor.Decrypt(bytes).Elf);
            if (operation == SelfFolderOperation.LegacySign) Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(bytes));
        }
        var np = SelfReader.ParseInfo(new MemoryStream(packed.TryReadEntryBytes("USRDIR/module.sprx")!));
        Assert.Equal(DevKlicFixture.ContentId, np.Npdrm!.ContentId);
        Assert.Equal(3u, np.Npdrm.RawLicenseType);
        Assert.Equal("unchanged"u8.ToArray(), packed.TryReadEntryBytes("DATA.BIN"));
        Assert.Equal(npdrm, File.ReadAllBytes(Path.Combine(f.Source, "USRDIR/module.sprx")));
        Assert.Equal(elf, File.ReadAllBytes(Path.Combine(f.Source, "USRDIR/EBOOT.BIN")));
    }

    [Fact]
    public void RepackIncludesPendingExecutableAndSfoEditsWithoutMutatingSession()
    {
        using var f = new Fixture();
        byte[] elf = DevKlicFixture.Elf(1024), editedElf = DevKlicFixture.Elf(2048);
        byte[] original = new SyntheticPkgBuilder()
            .AddFile("USRDIR/EBOOT.BIN", SelfBuilder.MakeFakeSelf(elf, false))
            .AddFile("PARAM.SFO", SfoWriter.Write([new() { Key = "TITLE", Format = SfoFormat.Utf8, Value = "Original" }]))
            .AddFile("data.bin", "old").Build();
        string source = f.Write("source.pkg", original);
        using var package = PackageOperationService.Open(source, new InMemoryKeyProvider());
        package.ReplaceEntry(package.Info.Entries.Single(e => e.Name == "USRDIR/EBOOT.BIN"), editedElf);
        package.ReplaceEntry(package.Info.Entries.Single(e => e.Name == "data.bin"), "edited data"u8.ToArray());
        package.ApplySfoEdits([new() { Key = "TITLE", Format = SfoFormat.Utf8, Value = "Edited title" }]);
        using var prepared = PreparedPackageExecutables.ForPackage(package, new(SelfFolderOperation.LegacySign));
        Assert.True(prepared.CanBuild);
        string destination = Path.Combine(f.Root, "saved.pkg");
        package.SaveAs(destination, prepared);
        using var saved = PackageOperationService.Open(destination, new InMemoryKeyProvider());
        var rebuilt = saved.TryReadEntryBytes("USRDIR/EBOOT.BIN")!;
        Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(rebuilt));
        Assert.Equal(editedElf, SelfDecryptor.Decrypt(rebuilt).Elf);
        Assert.Equal("Edited title", saved.Sfo!.Title);
        Assert.Equal("edited data"u8.ToArray(), saved.TryReadEntryBytes("data.bin"));
        Assert.Equal(3, package.PendingChangeCount);
        Assert.Equal(editedElf, package.TryReadEntryBytes("USRDIR/EBOOT.BIN"));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void LicensedModulesResolveDifferentRapsBeforePacking()
    {
        using var f = new Fixture();
        byte[] elf = DevKlicFixture.Elf(1024);
        var keys = new List<byte[]>();
        for (int i = 0; i < 2; i++)
        {
            string id = $"UP0001-NPUB5432{i}_00-PACKLICENSE00001";
            byte[] rap = Enumerable.Repeat((byte)(33 + i), 16).ToArray();
            keys.Add(NpdKeys.RapToKlicensee(rap));
            File.WriteAllBytes(Path.Combine(f.Raps, id + ".rap"), rap);
            f.Add($"module{i}.sprx", EncryptedSelfBuilder.Build(elf, new()
            {
                Metadata = new() { Npdrm = true, NpLicenseType = 2, ContentId = id },
                Klicensee = keys[i], FileName = $"module{i}.sprx",
            }));
        }
        using var prepared = PreparedPackageExecutables.ForFolder(f.Source, new(SelfFolderOperation.LegacySign, RapDirectory: f.Raps));
        Assert.True(prepared.CanBuild);
        string path = Path.Combine(f.Root, "built.pkg"); BuildFolder(f.Source, path, prepared);
        using var package = PackageOperationService.Open(path, new InMemoryKeyProvider());
        for (int i = 0; i < 2; i++)
        {
            byte[] self = package.TryReadEntryBytes($"module{i}.sprx")!;
            Assert.Equal(elf, SelfDecryptor.Decrypt(self, keys[i]).Elf);
            Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(self, keys[i]));
            Assert.Equal(2u, SelfReader.ParseInfo(new MemoryStream(self)).Npdrm!.RawLicenseType);
        }
    }

    [Fact]
    public void MissingKeysAndMalformedFilesBlockTheWholeNewWorkflow()
    {
        using var f = new Fixture();
        f.Add("EBOOT.BIN", EncryptedSelfBuilder.Build(DevKlicFixture.Elf(1024), new()
        { Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 2 }, Klicensee = DevKlicFixture.Key }));
        f.Add("broken.self", "broken"u8.ToArray());
        f.Add("good.self", SelfBuilder.MakeFakeSelf(DevKlicFixture.Elf(1024), false));
        using var prepared = PreparedPackageExecutables.ForFolder(f.Source, new(SelfFolderOperation.Rebuild, RapDirectory: f.Raps));
        Assert.False(prepared.CanBuild);
        Assert.Equal(2, prepared.Checks.Count(c => c.Status == "Blocked"));
        Assert.Single(prepared.Checks, c => c.Status == "Ready");
        string destination = f.Write("existing.pkg", "keep"u8.ToArray());
        Assert.Throws<PkgFormatException>(() => BuildFolder(f.Source, destination, prepared));
        Assert.Equal("keep"u8.ToArray(), File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData("change")]
    [InlineData("add")]
    [InlineData("remove")]
    public void ChangedFolderExecutablesRequireAnotherCheck(string mutation)
    {
        using var f = new Fixture();
        f.Add("EBOOT.BIN", DevKlicFixture.Elf(1024));
        using var prepared = PreparedPackageExecutables.ForFolder(f.Source, new(SelfFolderOperation.Rebuild));
        if (mutation == "change") f.Add("EBOOT.BIN", DevKlicFixture.Elf(2048));
        if (mutation == "add") f.Add("other.self", DevKlicFixture.Elf(1024));
        if (mutation == "remove") File.Delete(Path.Combine(f.Source, "EBOOT.BIN"));
        Assert.Throws<PkgFormatException>(() => prepared.ValidateFolder(f.Source, default));
    }

    [Fact]
    public void ChangedPendingExecutableOrUnsupportedProfileNeverReplacesDestination()
    {
        using var f = new Fixture();
        string source = f.Write("source.pkg", new SyntheticPkgBuilder().AddFile("EBOOT.BIN", DevKlicFixture.Elf(1024)).Build());
        using var package = PackageOperationService.Open(source, new InMemoryKeyProvider());
        using var prepared = PreparedPackageExecutables.ForPackage(package, new(SelfFolderOperation.Rebuild));
        string destination = f.Write("saved.pkg", "keep"u8.ToArray());
        package.ReplaceEntry(package.Info.Entries.Single(e => e.IsFile), DevKlicFixture.Elf(2048));
        Assert.Throws<PkgFormatException>(() => package.SaveAs(destination, prepared));
        using var unsupported = PreparedPackageExecutables.ForPackage(package, new(SelfFolderOperation.LegacySign, 0x10));
        Assert.False(unsupported.CanBuild);
        Assert.Throws<PkgFormatException>(() => package.SaveAs(destination, unsupported));
        Assert.Equal("keep"u8.ToArray(), File.ReadAllBytes(destination));
        Assert.Equal(1, package.PendingChangeCount);
    }

    [Fact]
    public void TamperedPreparedOutputFailsEmbeddedVerificationBeforePublication()
    {
        using var f = new Fixture();
        string source = f.Write("source.pkg", new SyntheticPkgBuilder().AddFile("EBOOT.BIN", DevKlicFixture.Elf(1024)).Build());
        using var package = PackageOperationService.Open(source, new InMemoryKeyProvider());
        using var prepared = PreparedPackageExecutables.ForPackage(package, new(SelfFolderOperation.LegacySign));
        var replacement = prepared.Files["EBOOT.BIN"];
        string temporary;
        using (var stream = (FileStream)replacement.OpenRead()) temporary = stream.Name;
        byte[] changed = File.ReadAllBytes(temporary); changed[^1] ^= 0x55; File.WriteAllBytes(temporary, changed);
        string destination = f.Write("saved.pkg", "keep"u8.ToArray());
        Assert.Throws<PkgFormatException>(() => package.SaveAs(destination, prepared));
        Assert.Equal("keep"u8.ToArray(), File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
        prepared.Dispose();
        Assert.False(File.Exists(temporary));
        Assert.False(prepared.CanBuild);
    }

    [Fact]
    public void CancelledRepackPreservesDestinationAndSource()
    {
        using var f = new Fixture();
        string source = f.Write("source.pkg", new SyntheticPkgBuilder().AddFile("EBOOT.BIN", DevKlicFixture.Elf(1024)).Build());
        using var package = PackageOperationService.Open(source, new InMemoryKeyProvider());
        using var prepared = PreparedPackageExecutables.ForPackage(package, new(SelfFolderOperation.Rebuild));
        string destination = f.Write("saved.pkg", "keep"u8.ToArray());
        Assert.Throws<OperationCanceledException>(() => package.SaveAs(destination, prepared, new CancellationToken(true)));
        Assert.Equal("keep"u8.ToArray(), File.ReadAllBytes(destination));
        Assert.Throws<IOException>(() => package.SaveAs(source, prepared));
    }

    [AvaloniaFact]
    public async Task DialogBlocksFailuresAndAllowsRetryAfterSupplyingKey()
    {
        using var f = new Fixture();
        f.Add("EBOOT.BIN", EncryptedSelfBuilder.Build(DevKlicFixture.Elf(1024), new()
        { Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 2 }, Klicensee = DevKlicFixture.Key }));
        var dialog = new PackageSelfDialog(f.Raps, 3,
            (settings, token, progress) => PreparedPackageExecutables.ForFolder(f.Source, settings, token, progress));
        dialog.Show(); Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.FindControl<Button>("ContinueButton")!.IsEnabled);
        await dialog.CheckAsync();
        Assert.False(dialog.FindControl<Button>("ContinueButton")!.IsEnabled);
        Assert.Contains("blocked", dialog.FindControl<TextBlock>("StatusText")!.Text);
        dialog.FindControl<TextBox>("KeyBox")!.Text = Convert.ToHexString(DevKlicFixture.Key);
        Dispatcher.UIThread.RunJobs();
        await dialog.CheckAsync();
        Assert.True(dialog.FindControl<Button>("ContinueButton")!.IsEnabled);
        dialog.FindControl<TextBox>("RevisionBox")!.Text = "04";
        Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.FindControl<Button>("ContinueButton")!.IsEnabled);
        dialog.FindControl<ComboBox>("ModeBox")!.SelectedIndex = 0;
        Assert.True(dialog.FindControl<Button>("ContinueButton")!.IsEnabled);
        dialog.Close();
    }

    [Fact]
    public void CancellingPreparationNeverReturnsAPartialSuccess()
    {
        using var f = new Fixture();
        f.Add("a.self", DevKlicFixture.Elf(1024));
        f.Add("b.self", DevKlicFixture.Elf(1024));
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => PreparedPackageExecutables.ForFolder(f.Source,
            new(SelfFolderOperation.Rebuild), cancellation.Token,
            new ImmediateProgress(check => { if (check.Status == "Ready") cancellation.Cancel(); })));
        Assert.Equal(DevKlicFixture.Elf(1024), File.ReadAllBytes(Path.Combine(f.Source, "a.self")));
        using var retry = PreparedPackageExecutables.ForFolder(f.Source, new(SelfFolderOperation.Rebuild));
        Assert.True(retry.CanBuild);
        Assert.Equal(2, retry.Checks.Count);
    }

    private sealed class ImmediateProgress(Action<PackageSelfCheck> report) : IProgress<PackageSelfCheck>
    { public void Report(PackageSelfCheck value) => report(value); }

    private static void BuildFolder(string source, string destination, PreparedPackageExecutables prepared)
    {
        prepared.ValidateFolder(source, default);
        var plan = FolderPackage.Plan(source, new() { ContentId = DevKlicFixture.ContentId, PreparedFiles = prepared.Files });
        var keys = new InMemoryKeyProvider();
        AtomicOutput.Write(destination, stream => plan.Builder.Build(stream, keys), validate: temporary =>
        {
            PostOperationVerifier.VerifyPackage(temporary, keys);
            prepared.VerifyEmbedded(temporary, keys, default);
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pkglens-package-self-test-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "input");
        internal string Raps => Path.Combine(Root, "raps");
        internal Fixture() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Raps); }
        internal void Add(string name, byte[] bytes)
        { string path = Path.Combine(Source, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
        internal string Write(string name, byte[] bytes)
        { string path = Path.Combine(Root, name); File.WriteAllBytes(path, bytes); return path; }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
