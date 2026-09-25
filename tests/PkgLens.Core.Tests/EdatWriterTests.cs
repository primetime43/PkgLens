using System.Buffers.Binary;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Gui.Services;

namespace PkgLens.Core.Tests;

public sealed class EdatWriterTests
{
    private const string ContentId = "UP0001-NPUB12345_00-EDATTEST00000001";
    private static readonly byte[] Key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Plain = Enumerable.Range(0, 35001).Select(i => (byte)(i * 17 + 3)).ToArray();
    public static IEnumerable<object[]> Formats() =>
        from version in new[] { 1, 2, 3, 4 }
        from format in new[] { "free", "licensed", "sdat" }
        where version != 1 || format != "sdat"
        select new object[] { version, format };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Writer_RoundTripsAcrossVersionsAndFormats(int version, string format)
    {
        var options = Options() with { Version = version, IsSdat = format == "sdat", License = format == "licensed" ? 2 : 3, DeveloperKey = Key };
        using var output = new MemoryStream();
        EdatWriter.WriteVerified(new MemoryStream(Plain), output, options, Key);
        var header = EdatFile.ParseHeader(output);
        Assert.Equal(version, header.Version);
        Assert.Equal(format == "sdat", header.IsSdat);
        Assert.Equal((long)Plain.Length, header.FileSize);
        Assert.Equal(Plain, EdatFile.DecryptToArray(output, Key));
        byte[] data = output.ToArray();
        Assert.All(data[0xB0..0x100], value => Assert.Equal(0, value)); // No forged claims of Sony signatures.
        Assert.StartsWith(format == "sdat" ? "SDATA " : "EDATA ", System.Text.Encoding.ASCII.GetString(data[^16..]));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(1024)] [InlineData(1025)]
    public void Writer_HandlesEmptyAndPartialBlocks(int size)
    {
        using var output = new MemoryStream();
        EdatWriter.WriteVerified(new MemoryStream(Plain[..size]), output, Options() with { BlockSize = 1024 });
        Assert.Equal(Plain[..size], EdatFile.DecryptToArray(output));
    }

    [Fact]
    public void CustomFreeKey_IsRequiredAndUsedForEncryption()
    {
        using var output = new MemoryStream();
        EdatWriter.WriteVerified(new MemoryStream(Plain), output, Options(), Key);
        Assert.Equal(Plain, EdatFile.DecryptToArray(output, Key));
        Assert.Throws<PkgFormatException>(() => EdatFile.DecryptToArray(output));
    }

    [Fact]
    public void QuickRebuild_ReplacesPlaintextAndPreservesIdentity()
    {
        using var fixture = new Fixture();
        byte[] original = Build(Plain, Options() with { License = 2 }, Key);
        File.WriteAllBytes(fixture.Source, original);
        File.WriteAllBytes(fixture.Replacement, "Changed plaintext"u8.ToArray());
        var request = fixture.Request(EdatWorkbenchOperation.QuickRebuild) with { ReplacementPath = fixture.Replacement };
        using var result = EdatWorkbenchService.Build(request);
        byte[] rebuilt = result.ReadForStaging();
        Assert.Equal(original[..0x80], rebuilt[..0x80]);
        Assert.Equal("Changed plaintext"u8.ToArray(), EdatFile.DecryptToArray(new MemoryStream(rebuilt), Key));
        Assert.Equal(original, File.ReadAllBytes(fixture.Source));
        Assert.Throws<IOException>(() => result.SaveCopy(fixture.Source, fixture.Source));
        Assert.Throws<IOException>(() => result.SaveCopy(Path.Combine(fixture.Root, "renamed.edat"), fixture.Source));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "out"));
        string copy = Path.Combine(fixture.Root, "out", "source.edat");
        result.SaveCopy(copy, fixture.Source);
        Assert.Equal(rebuilt, File.ReadAllBytes(copy));
    }

    [Fact]
    public void QuickRebuild_RejectsRename_CustomRebuildCanChangeFormatAndContentId()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Source, Build(Plain, Options() with { License = 2 }, Key));
        Assert.Throws<PkgFormatException>(() => EdatWorkbenchService.Build(fixture.Request(EdatWorkbenchOperation.QuickRebuild) with { OutputFileName = "renamed.edat" }));
        using var result = EdatWorkbenchService.Build(fixture.Request(EdatWorkbenchOperation.CustomRebuild) with
        {
            OutputFileName = "converted.sdat", Options = Options() with { Version = 4, IsSdat = true, ContentId = "" },
        });
        using var output = File.OpenRead(result.OutputPath);
        Assert.True(EdatFile.ParseHeader(output).IsSdat);
        Assert.Equal(Plain, EdatFile.DecryptToArray(output));
    }

    [Fact]
    public void CorruptedOriginal_IsRejectedEvenWhenReplacementWasProvided()
    {
        using var fixture = new Fixture();
        byte[] source = Build(Plain, Options());
        source[0x121] ^= 0x80;
        File.WriteAllBytes(fixture.Source, source);
        File.WriteAllBytes(fixture.Replacement, Plain);
        Assert.Throws<PkgFormatException>(() => EdatWorkbenchService.Build(fixture.Request(EdatWorkbenchOperation.QuickRebuild) with
        { InputKey = new(), ReplacementPath = fixture.Replacement }));
        Assert.Equal(source, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public void CancelDuringEncryption_StopsBeforeVerification()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => EdatWriter.WriteVerified(new MemoryStream(Plain), output,
            Options(), token: cancellation.Token, progress: new CancelProgress(cancellation)));
    }

    [Theory]
    [InlineData(1, true, 1024)] [InlineData(5, false, 1024)] [InlineData(3, false, 1000)]
    public void InvalidSettings_AreRejectedBeforeOutput(int version, bool sdat, int block)
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => EdatWriter.WriteVerified(new MemoryStream(Plain), output,
            Options() with { Version = version, IsSdat = sdat, BlockSize = block }));
        Assert.Equal(0, output.Length);
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<double>
    { public void Report(double value) => cancellation.Cancel(); }

    [Fact]
    public void InstalledRap_EncryptsAndDecrypts_AndCancelledSaveKeepsDestination()
    {
        using var fixture = new Fixture();
        string rapDirectory = Path.Combine(fixture.Root, "raps");
        Directory.CreateDirectory(rapDirectory);
        File.WriteAllBytes(Path.Combine(rapDirectory, ContentId + ".rap"), Key);
        File.WriteAllBytes(fixture.Source, Plain);
        using var encrypted = EdatWorkbenchService.Build(fixture.Request(EdatWorkbenchOperation.Encrypt) with
        { Options = Options() with { License = 2 }, InputKey = new(), OutputKey = new() });
        string encryptedPath = Path.Combine(fixture.Root, "out", "source.edat");
        Directory.CreateDirectory(Path.GetDirectoryName(encryptedPath)!);
        encrypted.SaveCopy(encryptedPath, fixture.Source);
        using var decrypted = EdatWorkbenchService.Build(fixture.Request(EdatWorkbenchOperation.Decrypt) with
        { SourcePath = encryptedPath, InputKey = new(), OutputFileName = "plaintext.bin" });
        Assert.Equal(Plain, File.ReadAllBytes(decrypted.PlaintextPath));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        byte[] before = File.ReadAllBytes(encryptedPath);
        Assert.Throws<OperationCanceledException>(() => encrypted.SaveCopy(encryptedPath, fixture.Source, token: cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(encryptedPath));
        string scratch = decrypted.DirectoryPath;
        decrypted.Dispose();
        Assert.False(Directory.Exists(scratch));
    }
    private static EdatWriteOptions Options() => new() { ContentId = ContentId, FileName = "source.edat", BlockSize = 1024 };
    private static byte[] Build(byte[] plaintext, EdatWriteOptions options, byte[]? key = null)
    {
        using var output = new MemoryStream();
        EdatWriter.WriteVerified(new MemoryStream(plaintext), output, options, key);
        return output.ToArray();
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pkglens-edat-test-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source.edat");
        public string Replacement => Path.Combine(Root, "replacement.bin");
        public Fixture() => Directory.CreateDirectory(Root);
        public EdatWorkbenchRequest Request(EdatWorkbenchOperation operation) => new(operation, Source, "source.edat",
            Options(), new(Convert.ToHexString(Key)), new(), KlicenseeDatabasePath: Path.Combine(Root, "keys.json"), RapDirectory: Path.Combine(Root, "raps"));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
