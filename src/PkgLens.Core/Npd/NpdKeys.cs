using System.Security.Cryptography;

namespace PkgLens.Core.Npd;

/// <summary>
/// Public NPDRM/EDAT decryption constants and the RAP→klicensee conversion. These are decryption
/// (not signing) constants, published for years and required for EDAT/SDAT decryption; embedding
/// them is consistent with "decrypt content you own." Algorithm ported from make_npdata (GPL) and
/// verified byte-for-byte against a real retail EDAT.
/// </summary>
public static class NpdKeys
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    /// <summary>SDAT self-key XOR constant: SDAT crypt key = dev_hash XOR this.</summary>
    public static readonly byte[] SdatKey = Hex("0D655EF8E674A98AB8505CFA7D012933");

    /// <summary>Free/dev klicensee, used for DRM type 3 (free) EDATs.</summary>
    public static readonly byte[] KlicFree = Hex("72F990788F9CFF745725F08E4C128387");

    /// <summary>Version-selected EDAT cipher keys (0 for v&lt;4, 1 for v4).</summary>
    public static readonly byte[] EdatKey0 = Hex("BE959CA8308DEFA2E5E180C63712A9AE");
    public static readonly byte[] EdatKey1 = Hex("4CA9C14B01C95309969BEC68AA0BC081");

    public static readonly byte[] EdatHash0 = Hex("EFFE5BD1652EEBC11918CF7C04D4F011");
    public static readonly byte[] EdatHash1 = Hex("3D92699B705B073854D8FCC6C7672747");

    // RAP → klicensee (rap2rifkey) constants.
    private static readonly byte[] RapKey = Hex("869F7745C13FD890CCF29188E3CC3EDF");
    private static readonly byte[] RapE1 = Hex("A93E1FD67C55A329B75FDDA62A95C7A5");
    private static readonly byte[] RapE2 = Hex("67D45DA3296D006A4E7C537BF5538C74");
    private static readonly byte[] RapPbox =
        { 0x0C, 0x03, 0x06, 0x04, 0x01, 0x0B, 0x0F, 0x08, 0x02, 0x07, 0x00, 0x05, 0x0A, 0x0E, 0x0D, 0x09 };

    /// <summary>Converts a 16-byte RAP (license file) to the 16-byte klicensee used to decrypt EDATs.</summary>
    public static byte[] RapToKlicensee(ReadOnlySpan<byte> rap)
    {
        if (rap.Length != 16)
            throw new ArgumentException("A RAP must be exactly 16 bytes.", nameof(rap));

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = RapKey;
        byte[] key = aes.DecryptEcb(rap, PaddingMode.None); // CBC with zero IV over one block == ECB

        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 16; i++) { int p = RapPbox[i]; key[p] ^= RapE1[p]; }
            for (int i = 15; i >= 1; i--) { int p = RapPbox[i]; int pp = RapPbox[i - 1]; key[p] ^= key[pp]; }

            int o = 0;
            for (int i = 0; i < 16; i++)
            {
                int p = RapPbox[i];
                byte kc = (byte)(key[p] - o);
                byte ec2 = RapE2[p];
                if (o != 1 || kc != 0xFF) { o = kc < ec2 ? 1 : 0; key[p] = (byte)(kc - ec2); }
                else if (kc == 0xFF) key[p] = (byte)(kc - ec2);
                else key[p] = kc;
            }
        }
        return key;
    }
}
