using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PkgLens.Core.Ps3.Trophy;
using PkgLens.Core.Shared;

namespace PkgLens.Core.Tests;

public sealed class TrophyArchiveTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Read_ParsesIdentityCountsDetailsAndArtwork()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <trophyconf version="1.1">
              <title-name>Example Game</title-name>
              <title-detail>Example trophy set</title-detail>
              <trophy id="0" hidden="no" ttype="P" pid="-1">
                <name>Master Collector</name><detail>Earn every trophy.</detail>
              </trophy>
              <trophy id="1" hidden="yes" ttype="G" pid="0">
                <name>Secret Gold</name><detail>Find the secret.</detail>
              </trophy>
              <trophy id="2" hidden="false" ttype="S" pid="0">
                <name>Silver Step</name><detail>Keep going.</detail>
              </trophy>
              <trophy id="3" hidden="0" ttype="B" pid="0">
                <name>First Step</name><detail>Begin the game.</detail>
              </trophy>
            </trophyconf>
            """;
        byte[] archive = BuildArchive(
            ("TROPCONF.SFM", Encoding.UTF8.GetBytes("<trophyconf><title-name>Wrong language</title-name></trophyconf>")),
            ("TROP_01.SFM", Encoding.UTF8.GetBytes(xml)),
            ("ICON0.PNG", OnePixelPng),
            ("TROP001.PNG", OnePixelPng));

        TrophySet set = TrophyArchive.Read(archive, "NPWR12345_00");

        Assert.Equal("NPWR12345_00", set.Id);
        Assert.Equal("Example Game", set.Name);
        Assert.Equal("Example trophy set", set.Description);
        Assert.Equal("TROP_01.SFM", set.MetadataFileName);
        Assert.Equal(2u, set.ArchiveVersion);
        Assert.True(set.ChecksumVerified);
        Assert.Equal(1, set.BronzeCount);
        Assert.Equal(1, set.SilverCount);
        Assert.Equal(1, set.GoldCount);
        Assert.Equal(1, set.PlatinumCount);
        Assert.Equal(OnePixelPng, set.Artwork);

        TrophyItem secret = Assert.Single(set.Trophies, trophy => trophy.Id == 1);
        Assert.Equal("Secret Gold", secret.Name);
        Assert.Equal("Find the secret.", secret.Description);
        Assert.Equal(TrophyGrade.Gold, secret.Grade);
        Assert.True(secret.IsHidden);
        Assert.Equal(OnePixelPng, secret.Icon);
        Assert.Null(Assert.Single(set.Trophies, trophy => trophy.Id == 0).Icon);
    }

    [Fact]
    public void Read_RejectsChecksumMismatch()
    {
        byte[] archive = BuildArchive(("TROPCONF.SFM",
            Encoding.UTF8.GetBytes("<trophyconf><title-name>Example</title-name></trophyconf>")));
        archive[^1] ^= 0xff;

        PkgFormatException exception = Assert.Throws<PkgFormatException>(() => TrophyArchive.Read(archive));
        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_PrefersTextMetadataOverReducedTrophyConfiguration()
    {
        byte[] archive = BuildArchive(
            ("TROPCONF.SFM", Encoding.UTF8.GetBytes("""
                <trophyconf version="1.1">
                  <npcommid>NPWR04268_00</npcommid>
                  <trophy id="0" hidden="no" ttype="B" pid="-1" />
                </trophyconf>
                """)),
            ("TROP.SFM", Encoding.UTF8.GetBytes("""
                <trophyconf version="1.1">
                  <npcommid>NPWR04268_00</npcommid>
                  <title-name>Farming Simulator</title-name>
                  <title-detail>Farming Simulator trophies</title-detail>
                  <trophy id="0" hidden="no" ttype="B" pid="-1">
                    <name>Newcomer</name>
                    <detail>Earn your first million.</detail>
                  </trophy>
                </trophyconf>
                """)));

        TrophySet set = TrophyArchive.Read(archive);

        Assert.Equal("TROP.SFM", set.MetadataFileName);
        Assert.Equal("Farming Simulator", set.Name);
        Assert.Equal("Farming Simulator trophies", set.Description);
        TrophyItem trophy = Assert.Single(set.Trophies);
        Assert.Equal("Newcomer", trophy.Name);
        Assert.Equal("Earn your first million.", trophy.Description);
    }

    [Fact]
    public void Read_RejectsEntryOutsideArchive()
    {
        byte[] archive = BuildArchive(new[]
        {
            ("TROPCONF.SFM",
                Encoding.UTF8.GetBytes("<trophyconf><title-name>Example</title-name></trophyconf>")),
        }, version: 1);
        BinaryPrimitives.WriteUInt64BigEndian(archive.AsSpan(TrophyArchive.HeaderSize + 0x20),
            (ulong)archive.Length + 1);

        PkgFormatException exception = Assert.Throws<PkgFormatException>(() => TrophyArchive.Read(archive));
        Assert.Contains("bounds", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static byte[] BuildArchive(params (string Name, byte[] Data)[] entries) =>
        BuildArchive(entries, version: 2);

    private static byte[] BuildArchive((string Name, byte[] Data)[] entries, uint version)
    {
        int dataOffset = TrophyArchive.HeaderSize + entries.Length * TrophyArchive.EntrySize;
        int fileSize = dataOffset + entries.Sum(entry => entry.Data.Length);
        var result = new byte[fileSize];
        BinaryPrimitives.WriteUInt32BigEndian(result, TrophyArchive.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0x04), version);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(0x08), (ulong)fileSize);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0x10), (uint)entries.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0x14), TrophyArchive.EntrySize);

        int cursor = dataOffset;
        for (int index = 0; index < entries.Length; index++)
        {
            (string name, byte[] content) = entries[index];
            int record = TrophyArchive.HeaderSize + index * TrophyArchive.EntrySize;
            Encoding.ASCII.GetBytes(name).CopyTo(result, record);
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(record + 0x20), (ulong)cursor);
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(record + 0x28), (ulong)content.Length);
            content.CopyTo(result, cursor);
            cursor += content.Length;
        }

        if (version >= 2)
        {
            byte[] hash = SHA1.HashData(result);
            hash.CopyTo(result, 0x1c);
        }
        return result;
    }
}
