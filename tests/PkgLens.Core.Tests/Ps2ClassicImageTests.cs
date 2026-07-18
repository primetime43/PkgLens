using PkgLens.Core;
using PkgLens.Core.Ps2;
using PkgLens.Core.Ps3.Npd;
using System.Security.Cryptography;

namespace PkgLens.Core.Tests;

public class Ps2ClassicImageTests
{
    [Fact]
    public void Encrypt_CexReferenceVector_MatchesPublishedAlgorithm()
    {
        byte[] plaintext = Enumerable.Range(0, 0x4000)
            .Select(index => (byte)(index * 29 + 7)).ToArray();
        using var encrypted = new MemoryStream();

        Ps2ClassicImage.Encrypt(new MemoryStream(plaintext), encrypted,
            Ps2ClassicImage.PlaceholderKlicensee);

        byte[] payload = encrypted.ToArray()[0x4000..];
        Assert.Equal("7136BDE6B3E20E666061D4593C8843CC58CDA8D55F1B763FC86377C1E4816399",
            Convert.ToHexString(SHA256.HashData(payload)));
    }

    [Fact]
    public void EncryptDecrypt_CrossesMetadataGroup_RoundTripsExactly()
    {
        byte[] plaintext = Enumerable.Range(0, 0x4000 * 513 + 123)
            .Select(index => (byte)(index * 29 + 7)).ToArray();
        using var encrypted = new MemoryStream();
        Ps2ClassicImage.Encrypt(new MemoryStream(plaintext), encrypted,
            Ps2ClassicImage.PlaceholderKlicensee);

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        Ps2ClassicImageInfo info = Ps2ClassicImage.Decrypt(encrypted, decrypted,
            Ps2ClassicImage.PlaceholderKlicensee);

        Assert.Equal(Ps2ClassicImage.PlaceholderContentId, info.ContentId);
        Assert.Equal(Ps2ClassicMode.Cex, info.Mode);
        Assert.Equal(plaintext, decrypted.ToArray());
    }

    [Fact]
    public void Decrypt_WrongKlicensee_IsReportedAsLicenseFailure()
    {
        using var encrypted = new MemoryStream();
        Ps2ClassicImage.Encrypt(new MemoryStream(new byte[0x5000]), encrypted,
            Ps2ClassicImage.PlaceholderKlicensee);
        encrypted.Position = 0;
        byte[] wrong = Ps2ClassicImage.PlaceholderKlicensee.ToArray();
        wrong[0] ^= 0x80;

        PkgKeyException error = Assert.Throws<PkgKeyException>(() =>
            Ps2ClassicImage.Decrypt(encrypted, new MemoryStream(), wrong));

        Assert.Contains("RAP/klicensee", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_CorruptSegment_IsRejected()
    {
        using var encrypted = new MemoryStream();
        Ps2ClassicImage.Encrypt(new MemoryStream(new byte[0x5000]), encrypted,
            Ps2ClassicImage.PlaceholderKlicensee);
        byte[] data = encrypted.ToArray();
        data[0x8000 + 19] ^= 0x40;

        Assert.Throws<PkgKeyException>(() => Ps2ClassicImage.Decrypt(
            new MemoryStream(data), new MemoryStream(), Ps2ClassicImage.PlaceholderKlicensee));
    }

    [Fact]
    public void EdatBuilder_CreatesAuthenticatedLicensedRecord()
    {
        byte[] plaintext = "SLUS_12345"u8.ToArray();
        byte[] edat = EdatBuilder.BuildLicensed(plaintext, Ps2ClassicImage.PlaceholderContentId,
            "ISO.BIN.EDAT", Ps2ClassicImage.PlaceholderKlicensee);

        byte[] decrypted = EdatFile.DecryptToArray(new MemoryStream(edat),
            Ps2ClassicImage.PlaceholderKlicensee);

        Assert.Equal(plaintext, decrypted);
    }
}
