using System.Buffers.Binary;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests;

public class PkgFinalizerTests
{
    private static readonly byte[] TestKey = Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();

    [Theory]
    [InlineData(0, false)]
    [InlineData(17, true)]
    [InlineData(1048613, false)]
    [InlineData(1048613, true)]
    public void StreamsRetailCopyPreservingLayoutMetadataAndFileBytes(int size, bool footer)
    {
        byte[] data = Enumerable.Range(0, size).Select(i => (byte)(i * 13)).ToArray();
        byte[] original = new SyntheticPkgBuilder().AddMetadataString(0x0A, "NPUB30910")
            .AddMetadataU32(0x01, 2).AddMetadataU32(0x02, 5).AddMetadataU32(0xABC, 42)
            .AddDirectory("USRDIR").AddFile("USRDIR/DATA.BIN", data).AddFile("empty.bin", []).Build();
        if (footer) original = WithFooter(original);
        using var source = new MemoryStream(original, writable: false);
        using var output = new MemoryStream();
        var keys = new InMemoryKeyProvider(TestKey);
        var report = PkgFinalizer.Convert(source, output, keys);
        PkgFinalizer.Verify(output, keys, report);
        Assert.Equal(2, report.FileCount);
        Assert.Equal(1, report.DirectoryCount);
        Assert.Equal(original, source.ToArray());
        var oldInfo = PkgReader.Read(source, new InMemoryKeyProvider());
        var newInfo = PkgReader.Read(output, keys);
        Assert.Equal(PkgFinalization.Retail, newInfo.Header.Finalization);
        Assert.Equal(oldInfo.Header.DataOffset, newInfo.Header.DataOffset);
        Assert.Equal(oldInfo.Header.DataSize, newInfo.Header.DataSize);
        Assert.Equal(oldInfo.Header.QaDigest, newInfo.Header.QaDigest);
        Assert.Equal(oldInfo.Header.DataRiv, newInfo.Header.DataRiv);
        Assert.Equal(oldInfo.Entries.Select(e => (e.Name, e.FileOffset, e.FileSize, e.RawType)),
            newInfo.Entries.Select(e => (e.Name, e.FileOffset, e.FileSize, e.RawType)));
        byte[] result = output.ToArray();
        int metadataLength = checked((int)oldInfo.Header.DataOffset - 0xC0);
        Assert.Equal(original.AsSpan(0xC0, metadataLength).ToArray(), result.AsSpan(0xC0, metadataLength).ToArray());
        Assert.Equal(data, PkgReader.ExtractEntryBytes(output, newInfo.Header,
            newInfo.Entries.Single(e => e.Name == "USRDIR/DATA.BIN"), keys));
        Assert.Equal(SHA1.HashData(result.AsSpan(0, result.Length - 32)), result.AsSpan(result.Length - 32, 20).ToArray());
        Assert.All(result.AsSpan(0x90, 0x28).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal((long)newInfo.Header.DataOffset + (long)newInfo.Header.DataSize + 128, result.LongLength);
    }

    [Fact]
    public void EmbeddedEncryptedSelfSignaturesAndLicenseContentsAreUnchanged()
    {
        byte[] elf = DevKlicFixture.Elf(2048);
        byte[] self = EncryptedSelfBuilder.Build(elf, new()
        {
            Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 2 },
            Klicensee = DevKlicFixture.Key, SignHeader = true,
        });
        byte[] original = new SyntheticPkgBuilder().AddFile("USRDIR/EBOOT.BIN", self)
            .AddFile("license.edat", new byte[] { 1, 2, 3, 4 }).Build();
        using var source = new MemoryStream(original);
        using var output = new MemoryStream();
        var keys = new InMemoryKeyProvider(TestKey);
        var report = PkgFinalizer.Convert(source, output, keys); // No executable key or RAP needed.
        PkgFinalizer.Verify(output, keys, report);
        var info = PkgReader.Read(output, keys);
        byte[] packed = PkgReader.ExtractEntryBytes(output, info.Header, info.Entries[0], keys);
        Assert.Equal(self, packed);
        Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(packed, DevKlicFixture.Key));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, PkgReader.ExtractEntryBytes(output, info.Header, info.Entries[1], keys));
    }

    [Theory]
    [InlineData("retail")]
    [InlineData("psp")]
    [InlineData("unknown")]
    [InlineData("truncated")]
    [InlineData("bounds")]
    [InlineData("trailer")]
    [InlineData("footer")]
    public void UnsupportedOrDamagedInputIsRejectedBeforeWriting(string kind)
    {
        byte[] bytes = new SyntheticPkgBuilder { Psp = kind == "psp", Finalization = kind == "retail" ? PkgFinalization.Retail : PkgFinalization.Debug }
            .AddFile("a.bin", "test").Build();
        if (kind == "unknown") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 0x1234);
        if (kind == "truncated") bytes = bytes[..^1];
        if (kind == "bounds") BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0x28), ulong.MaxValue);
        if (kind == "trailer") { Array.Resize(ref bytes, bytes.Length + 16); BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0x18), (ulong)bytes.Length); }
        if (kind == "footer") { bytes = WithFooter(bytes); bytes[^32] ^= 1; }
        using var source = new MemoryStream(bytes);
        using var output = new MemoryStream();
        Assert.Throws<PkgFormatException>(() => PkgFinalizer.Convert(source, output, new InMemoryKeyProvider(TestKey)));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void MissingRetailKeyIsReportedBeforeWriting()
    {
        using var source = new MemoryStream(new SyntheticPkgBuilder().AddFile("data", "abc").Build());
        using var output = new MemoryStream();
        Assert.Throws<PkgKeyException>(() => PkgFinalizer.Convert(source, output, new InMemoryKeyProvider()));
        Assert.Equal(0, output.Length);
        Assert.Throws<ArgumentException>(() => PkgFinalizer.Convert(source, source, new InMemoryKeyProvider(TestKey)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerificationRejectsAlteredDataEvenWithARecomputedFooter(bool rehash)
    {
        using var source = new MemoryStream(new SyntheticPkgBuilder().AddFile("data", "original payload").Build());
        using var output = new MemoryStream();
        var keys = new InMemoryKeyProvider(TestKey);
        var report = PkgFinalizer.Convert(source, output, keys);
        var info = PkgReader.Read(output, keys);
        byte[] tampered = output.ToArray();
        tampered[(int)(info.Header.DataOffset + info.Entries[0].FileOffset)] ^= 0x1;
        if (rehash) SHA1.HashData(tampered.AsSpan(0, tampered.Length - 32)).CopyTo(tampered, tampered.Length - 32);
        using var corrupt = new MemoryStream(tampered);
        Assert.Throws<PkgFormatException>(() => PkgFinalizer.Verify(corrupt, keys, report));
    }

    [Theory]
    [InlineData("Encrypting package")]
    [InlineData("Verifying contents and checksum")]
    public void CancellationDuringEncryptionOrVerificationPreservesExistingDestination(string stage)
    {
        using var f = new Files();
        byte[] original = new SyntheticPkgBuilder().AddFile("data", new byte[1048613]).Build();
        File.WriteAllBytes(f.Source, original);
        File.WriteAllText(f.Output, "keep");
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => PackageFinalizationService.FinalizeFile(f.Source, f.Output,
            cancellation.Token, new ImmediateProgress(p => { if (p.Item == stage) cancellation.Cancel(); })));
        Assert.Equal("keep", File.ReadAllText(f.Output));
        Assert.Equal(original, File.ReadAllBytes(f.Source));
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
    }

    [Fact]
    public void FileServicePublishesVerifiedCopyAndRejectsSamePath()
    {
        using var f = new Files();
        byte[] original = new SyntheticPkgBuilder().AddFile("data", "unchanged").Build();
        File.WriteAllBytes(f.Source, original);
        Assert.Throws<IOException>(() => PackageFinalizationService.FinalizeFile(f.Source, f.Source));
        var report = PackageFinalizationService.FinalizeFile(f.Source, f.Output);
        using var result = File.OpenRead(f.Output);
        PkgFinalizer.Verify(result, new InMemoryKeyProvider(BundledKeys.Ps3GpkgAesKey), report);
        Assert.Equal(original, File.ReadAllBytes(f.Source));
    }

    [AvaloniaFact]
    public async Task DialogChecksEligibilityAndFinalizesWithoutEditingOriginal()
    {
        using var f = new Files();
        File.WriteAllBytes(f.Source, new SyntheticPkgBuilder().AddFile("data", "test").Build());
        var dialog = new FinalizePackageDialog(); dialog.Show();
        Assert.False(dialog.FindControl<Button>("FinalizeButton")!.IsEnabled);
        await dialog.InspectAsync(f.Source);
        Assert.True(dialog.FindControl<Button>("FinalizeButton")!.IsEnabled);
        await dialog.FinalizeAsync(f.Output);
        Assert.Contains("Finalized and verified", dialog.FindControl<TextBlock>("StatusText")!.Text);
        Assert.True(dialog.FindControl<Button>("OpenOutputButton")!.IsVisible);
        await dialog.InspectAsync(f.Output);
        Assert.False(dialog.FindControl<Button>("FinalizeButton")!.IsEnabled);
        Assert.Contains("already retail-encrypted", dialog.FindControl<TextBlock>("StatusText")!.Text);
        dialog.Close();
    }

    private static byte[] WithFooter(byte[] original)
    {
        byte[] bytes = new byte[original.Length + 128]; original.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0x18), (ulong)bytes.Length);
        SHA1.HashData(bytes.AsSpan(0, 0x80)).AsSpan(12, 8).CopyTo(bytes.AsSpan(0xB8));
        SHA1.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
        return bytes;
    }
    private sealed class ImmediateProgress(Action<PkgOperationProgress> report) : IProgress<PkgOperationProgress>
    { public void Report(PkgOperationProgress value) => report(value); }
    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pkglens-finalize-test-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.pkg");
        internal string Output => Path.Combine(Root, "finalized.pkg");
        internal Files() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
