using System.Buffers.Binary;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;

namespace PkgLens.Core.Tests;

public sealed class DevKlicDiscoveryTests
{
    [Theory]
    [InlineData(false, false, 3, 1)]
    [InlineData(false, false, 2, 2)]
    [InlineData(true, false, 3, 3)]
    [InlineData(false, true, 3, 4)]
    public void FindsAndVerifiesUnalignedBinaryOrTextKeys_AndSavesExactMapping(bool hex, bool self, int license, int version)
    {
        using var fixture = new DevKlicFixture(hex, self, license: license, version: version);
        byte[] executable = File.ReadAllBytes(fixture.Executable), target = File.ReadAllBytes(fixture.Target);
        using var match = DevKlicDiscoveryService.Discover(fixture.Executable, fixture.Target, new(), fixture.Raps, fixture.Database);
        Assert.NotNull(match);
        Assert.Equal(0x113, match.ElfOffset);
        Assert.Equal(hex ? "Hexadecimal string" : "Raw 16-byte value", match.Representation);
        Assert.DoesNotContain(Convert.ToHexString(DevKlicFixture.Key), match.Description);
        Assert.False(File.Exists(fixture.Database));
        Assert.Equal(executable, File.ReadAllBytes(fixture.Executable));
        Assert.Equal(target, File.ReadAllBytes(fixture.Target));
        match.Save(fixture.Database);
        var mapping = KlicenseeStore.Find(DevKlicFixture.ContentId, "target.edat", (uint)license, fixture.Database);
        Assert.NotNull(mapping);
        Assert.Equal(DevKlicFixture.Key, mapping.Klicensee);
        Assert.Equal("target.edat", mapping.Entry.FileName);
        Assert.Equal((uint)license, mapping.Entry.LicenseType);
        Assert.Equal(EdatKeyCheckStatus.Verified,
            EdatKeyValidationService.Check(fixture.Target, new(), fixture.Raps, fixture.Database).Status);
    }

    [Fact]
    public void NoMatch_DoesNotInstallAnything()
    {
        using var fixture = new DevKlicFixture();
        File.WriteAllBytes(fixture.Executable, DevKlicFixture.Elf(512));
        Assert.Null(DevKlicDiscoveryService.Discover(fixture.Executable, fixture.Target, new()));
        Assert.False(File.Exists(fixture.Database));
    }

    [Fact]
    public void HeaderMatchWithDamagedContent_DoesNotReturnSavableKey()
    {
        using var fixture = new DevKlicFixture();
        byte[] target = File.ReadAllBytes(fixture.Target); target[0x120] ^= 0x80;
        File.WriteAllBytes(fixture.Target, target);
        var error = Assert.Throws<PkgFormatException>(() => DevKlicDiscoveryService.Discover(fixture.Executable, fixture.Target, new()));
        Assert.Contains("could not be confirmed", error.Message);
        Assert.False(File.Exists(fixture.Database));
    }

    [Fact]
    public void SaveRechecksChangedTarget_AndDisposedMatchCannotBeSaved()
    {
        using var fixture = new DevKlicFixture();
        using var match = DevKlicDiscoveryService.Discover(fixture.Executable, fixture.Target, new());
        Assert.NotNull(match);
        byte[] target = File.ReadAllBytes(fixture.Target); target[0x120] ^= 0x80;
        File.WriteAllBytes(fixture.Target, target);
        Assert.Throws<PkgFormatException>(() => match.Save(fixture.Database));
        Assert.False(File.Exists(fixture.Database));
        match.Dispose();
        Assert.Throws<ObjectDisposedException>(() => match.Save(fixture.Database));
    }

    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(0x01000000u)]
    public void UnverifiableTargetsAreRejected(uint flags)
    {
        using var fixture = new DevKlicFixture();
        byte[] target = File.ReadAllBytes(fixture.Target);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(0x80), flags);
        File.WriteAllBytes(fixture.Target, target);
        Assert.Throws<PkgFormatException>(() => DevKlicDiscoveryService.Discover(fixture.Executable, fixture.Target, new()));
    }

    [Fact]
    public void CancellationDuringScan_DoesNotProduceMatchOrDatabase()
    {
        using var fixture = new DevKlicFixture();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => DevKlicDiscoveryService.Discover(fixture.Executable, fixture.Target,
            new(), token: cancellation.Token, progress: new CancelProgress(cancellation)));
        Assert.False(File.Exists(fixture.Database));
    }

    [Fact]
    public void InvalidExecutableAndOversizedInput_AreRejected()
    {
        using var fixture = new DevKlicFixture();
        File.WriteAllText(fixture.Executable, "Not an executable");
        Assert.Throws<PkgFormatException>(() => DevKlicDiscoveryService.Discover(fixture.Executable, fixture.Target, new()));
        using (var large = File.OpenWrite(fixture.Executable)) large.SetLength(DevKlicDiscoveryService.MaxExecutableBytes + 1);
        Assert.Throws<IOException>(() => DevKlicDiscoveryService.Discover(fixture.Executable, fixture.Target, new()));
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<DevKlicProgress>
    { public void Report(DevKlicProgress value) { if (value.Phase.Contains("raw")) cancellation.Cancel(); } }
}
