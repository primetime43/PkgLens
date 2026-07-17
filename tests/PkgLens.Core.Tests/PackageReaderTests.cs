using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class PackageReaderTests
{
    private static byte[] BuildSfo() => new SfoBuilder()
        .AddString("TITLE", "Example Game")
        .AddString("TITLE_ID", "NPUB30910")
        .AddString("CATEGORY", "HG")
        .AddString("APP_VER", "01.02")
        .Build();

    [Fact]
    public void Read_DebugPackage_ListsEntriesAndSfo_NoKeyNeeded()
    {
        byte[] pkg = new SyntheticPkgBuilder()
            .AddDirectory("USRDIR")
            .AddFile("PARAM.SFO", BuildSfo())
            .AddFile("USRDIR/EBOOT.BIN", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })
            .AddMetadataU32((uint)PkgMetadataId.DrmType, 3)
            .AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.GameData)
            .Build();

        using var stream = new MemoryStream(pkg);
        var info = PkgReader.Read(stream, new InMemoryKeyProvider());

        Assert.True(info.IsDecrypted);
        Assert.Equal(3, info.Entries.Count);
        Assert.Contains(info.Entries, e => e.Name == "USRDIR" && e.IsDirectory);
        Assert.Contains(info.Entries, e => e.Name == "USRDIR/EBOOT.BIN" && e.IsFile);

        Assert.NotNull(info.Sfo);
        Assert.Equal("Example Game", info.Sfo!.Title);
        Assert.Equal("NPUB30910", info.Sfo.TitleId);

        Assert.Equal(3u, info.Metadata.DrmType);
        Assert.Equal(PkgContentType.GameData, info.Metadata.ContentType);
    }

    [Fact]
    public void Read_RetailPackage_WithCorrectKey_Decrypts()
    {
        var builder = new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
            .AddFile("PARAM.SFO", BuildSfo());
        byte[] pkg = builder.Build();

        using var stream = new MemoryStream(pkg);
        var info = PkgReader.Read(stream, new InMemoryKeyProvider(builder.RetailAesKey));

        Assert.True(info.IsDecrypted);
        Assert.Equal("Example Game", info.Sfo?.Title);
    }

    [Fact]
    public void Read_RetailPackage_WithoutKey_ReturnsHeaderButNotEntries()
    {
        byte[] pkg = new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
            .AddFile("PARAM.SFO", BuildSfo())
            .Build();

        using var stream = new MemoryStream(pkg);
        var info = PkgReader.Read(stream, new InMemoryKeyProvider(retailAesKey: null));

        Assert.False(info.IsDecrypted);
        Assert.Empty(info.Entries);
        Assert.Null(info.Sfo);
        Assert.NotNull(info.DecryptionNote);
        // Header-level data is still available without a key.
        Assert.Equal("NPUB30910", info.ContentId.TitleId);
    }

    [Fact]
    public void Read_RetailPackage_WithWrongKey_FailsGracefully()
    {
        var builder = new SyntheticPkgBuilder { Finalization = PkgFinalization.Retail }
            .AddFile("PARAM.SFO", BuildSfo());
        byte[] pkg = builder.Build();

        var wrongKey = new byte[16]; // all zeros, not the build key
        using var stream = new MemoryStream(pkg);

        // A wrong key yields garbage item-table bytes. The parser must degrade gracefully: either
        // it detects the resulting out-of-range structure (PkgFormatException) or it produces
        // garbage entries whose SFO cannot be parsed (Sfo == null). It must never crash with some
        // other unhandled exception type.
        try
        {
            var info = PkgReader.Read(stream, new InMemoryKeyProvider(wrongKey));
            Assert.Null(info.Sfo);
        }
        catch (PkgFormatException)
        {
            // Acceptable graceful failure.
        }
    }

    [Fact]
    public void ExtractEntry_ReturnsOriginalPlaintext()
    {
        var payload = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 + 7)).ToArray();
        byte[] pkg = new SyntheticPkgBuilder()
            .AddFile("USRDIR/DATA.BIN", payload)
            .Build();

        var header = PkgHeader.Parse(pkg);
        using var stream = new MemoryStream(pkg);
        var info = PkgReader.Read(stream, new InMemoryKeyProvider());
        var entry = info.Entries.Single(e => e.Name == "USRDIR/DATA.BIN");

        // Byte extraction.
        byte[] bytes = PkgReader.ExtractEntryBytes(stream, header, entry, new InMemoryKeyProvider());
        Assert.Equal(payload, bytes);

        byte[] range = PkgReader.ExtractEntryRange(stream, header, entry, new InMemoryKeyProvider(),
            entryOffset: 37, length: 211);
        Assert.Equal(payload.AsSpan(37, 211).ToArray(), range);

        // Streamed extraction with a small buffer to exercise chunk boundaries.
        using var dest = new MemoryStream();
        var dec = DecryptionContext.ForDebug(header).CreateDecryptor();
        PkgLens.Core.Shared.Formats.PkgContainerReader.CopyEntryTo(stream, header, dec, entry, dest, bufferSize: 64);
        Assert.Equal(payload, dest.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CopyEntryTo_NonpositiveBufferSize_ThrowsEvenForEmptyEntry(int bufferSize)
    {
        byte[] pkg = new SyntheticPkgBuilder().AddFile("EMPTY.BIN", Array.Empty<byte>()).Build();
        var header = PkgHeader.Parse(pkg);
        using var stream = new MemoryStream(pkg);
        var info = PkgReader.Read(stream, new InMemoryKeyProvider());
        var entry = info.Entries.Single(item => item.Name == "EMPTY.BIN");
        using var destination = new MemoryStream();
        var decryptor = DecryptionContext.ForDebug(header).CreateDecryptor();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PkgLens.Core.Shared.Formats.PkgContainerReader.CopyEntryTo(
                stream, header, decryptor, entry, destination, bufferSize));

        Assert.Equal("bufferSize", exception.ParamName);
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        var bytes = new byte[256];
        using var stream = new MemoryStream(bytes);
        Assert.Throws<PkgFormatException>(() => PkgReader.Read(stream, new InMemoryKeyProvider()));
    }

    [Fact]
    public void Read_Truncated_ThrowsGracefully()
    {
        byte[] pkg = new SyntheticPkgBuilder().AddFile("PARAM.SFO", BuildSfo()).Build();
        byte[] truncated = pkg.Take(pkg.Length / 2).ToArray();

        using var stream = new MemoryStream(truncated);
        Assert.Throws<PkgFormatException>(() => PkgReader.Read(stream, new InMemoryKeyProvider()));
    }

    [Fact]
    public void FileKeyProvider_ParsesHexAndRawKeyFiles()
    {
        var raw = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Assert.Equal(raw, FileKeyProvider.ParseKeyFile(raw));

        var hex = System.Text.Encoding.ASCII.GetBytes("000102030405060708090a0b0c0d0e0f");
        Assert.Equal(raw, FileKeyProvider.ParseKeyFile(hex));

        var hexPrefixed = System.Text.Encoding.ASCII.GetBytes("0x000102030405060708090A0B0C0D0E0F");
        Assert.Equal(raw, FileKeyProvider.ParseKeyFile(hexPrefixed));
    }
}
