using PkgLens.Core;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public class PostOperationVerifierTests
{
    [Fact]
    public void VerifyPackage_AcceptsReadableSyntheticPackage()
    {
        using var directory = new TempDirectory();
        string path = directory.Write("rebuilt.pkg",
            new SyntheticPkgBuilder().AddFile("USRDIR/DATA.BIN", "verified").Build());

        PostOperationVerificationReport report =
            PostOperationVerifier.VerifyPackage(path, new InMemoryKeyProvider());

        Assert.True(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "Item table" &&
            check.Status == PostVerificationStatus.Pass);
    }

    [Fact]
    public void VerifyPackage_RejectsTruncatedOutput()
    {
        using var directory = new TempDirectory();
        byte[] package = new SyntheticPkgBuilder().AddFile("DATA.BIN", new byte[64]).Build();
        string path = directory.Write("truncated.pkg", package[..^16]);

        PkgFormatException error = Assert.Throws<PkgFormatException>(() =>
            PostOperationVerifier.VerifyPackage(path, new InMemoryKeyProvider()));

        Assert.Contains("Post-operation verification failed", error.Message);
    }

    [Fact]
    public void VerifyPackage_ValidatesEmbeddedFakeSelfs()
    {
        using var directory = new TempDirectory();
        byte[] elf = MinimalElf.Build();
        string path = directory.Write("cfw.pkg", new SyntheticPkgBuilder()
            .AddFile("USRDIR/EBOOT.BIN", SelfBuilder.MakeFakeSelf(elf)).Build());

        PostOperationVerificationReport report = PostOperationVerifier.VerifyPackage(
            path, new InMemoryKeyProvider(), new[] { "USRDIR/EBOOT.BIN" });

        Assert.Contains(report.Checks, check => check.Name == "Fake SELF: USRDIR/EBOOT.BIN marker" &&
            check.Status == PostVerificationStatus.Pass);
        Assert.Throws<PkgFormatException>(() => PostOperationVerifier.VerifyPackage(
            path, new InMemoryKeyProvider(), new[] { "USRDIR/MISSING.SPRX" }));
    }

    [Fact]
    public void VerifyIso_AcceptsPrimaryVolumeDescriptor_AndRejectsCorruption()
    {
        using var directory = new TempDirectory();
        byte[] iso = BuildIso();
        string path = directory.Write("disc.iso", iso);

        Assert.True(PostOperationVerifier.VerifyIso(path, iso.Length).Passed);

        iso[16 * CsoWriter.BlockSize + 1] ^= 0xFF;
        File.WriteAllBytes(path, iso);
        Assert.Throws<PkgFormatException>(() => PostOperationVerifier.VerifyIso(path, iso.Length));
    }

    [Fact]
    public void VerifyPspExport_FullyDecodesCso_AndRejectsBadIndex()
    {
        using var directory = new TempDirectory();
        byte[] iso = BuildIso();
        string path = Path.Combine(directory.Path, "disc.cso");
        using (var source = new MemoryStream(iso))
        using (var destination = File.Create(path))
            CsoWriter.Compress(source, destination, iso.Length);

        long csoSize = new FileInfo(path).Length;
        var result = new PspExportResult(PspExportFormat.Cso, "content", "EBOOT.PBP",
            csoSize, IsoSize: iso.Length);
        PostOperationVerificationReport report = PostOperationVerifier.VerifyPspExport(path, result);
        Assert.True(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "CSO blocks" &&
            check.Status == PostVerificationStatus.Pass);

        byte[] corrupted = File.ReadAllBytes(path);
        Array.Clear(corrupted, 0x18, 4);
        File.WriteAllBytes(path, corrupted);
        Assert.Throws<PkgFormatException>(() => PostOperationVerifier.VerifyPspExport(path, result));
    }

    [Fact]
    public void VerifyFakeSelf_ConfirmsElfRoundTrip_AndRejectsWrongSource()
    {
        using var directory = new TempDirectory();
        byte[] elf = MinimalElf.Build();
        string path = directory.Write("EBOOT.BIN", SelfBuilder.MakeFakeSelf(elf));

        Assert.True(PostOperationVerifier.VerifyFakeSelf(path, elf).Passed);

        byte[] differentElf = (byte[])elf.Clone();
        differentElf[^1] ^= 0xFF;
        Assert.Throws<PkgFormatException>(() =>
            PostOperationVerifier.VerifyFakeSelf(path, differentElf));
    }

    private static byte[] BuildIso()
    {
        var iso = new byte[CsoWriter.BlockSize * 20];
        int descriptor = 16 * CsoWriter.BlockSize;
        iso[descriptor] = 1;
        "CD001"u8.CopyTo(iso.AsSpan(descriptor + 1));
        iso[descriptor + 6] = 1;
        for (int i = 0; i < iso.Length; i += 97)
            iso[i] ^= (byte)(i / 97);
        iso[descriptor] = 1;
        "CD001"u8.CopyTo(iso.AsSpan(descriptor + 1));
        iso[descriptor + 6] = 1;
        return iso;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pkglens-postverify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, byte[] data)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
