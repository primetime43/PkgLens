namespace PkgLens.Core.Shared.Keys;

/// <summary>
/// Bundled PS3/PSP/PSVita package-decryption AES keys. Symmetric decryption constants only — no
/// private/signing key, per-console secret (IDPS/EID), or per-purchase license (RAP), which the user
/// still supplies. Bundling lets retail packages decrypt out of the box.
/// </summary>
public static class BundledKeys
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    /// <summary>
    /// The standard NPDRM PKG PS3 AES key: decrypts retail (finalized) <c>.pkg</c> data (AES-128-CTR)
    /// and keys the header AES-CMAC. This is the default key for retail packages.
    /// </summary>
    public static readonly byte[] Ps3GpkgAesKey = Hex("2E7B71D7C9C9A14EA3221F188828B8F8");

    /// <summary>
    /// The NPDRM PKG PS3 IDU (in-store demo unit / kiosk) AES key. Not used automatically — IDU
    /// packages are not distinguished by header flags here — but bundled so it can be supplied as an
    /// override key file when reading a kiosk package.
    /// </summary>
    public static readonly byte[] Ps3IduAesKey = Hex("5DB911E6B7E50A7D321538FD7C66F17B");

    // ---- PSP / PSVita package keys (platform 0x0002) ----
    // Selected by key_type = header[0xE7] & 7. The PSP key is used directly (AES-128-CTR); the Vita
    // keys derive the per-package key via AES-ECB(key, data_riv).

    /// <summary>PSP / PSX package key (key_type 1). Used directly as the AES-128-CTR key.</summary>
    public static readonly byte[] PspPkgAesKey = Hex("07F2C68290B50D2C33818D709B60E62B");

    /// <summary>PSVita package key, revision 2 (key_type 2). Derives the CTR key via AES-ECB(key, data_riv).</summary>
    public static readonly byte[] VitaPkgAesKey2 = Hex("E31A70C9CE1DD72BF3C0622963F2ECCB");

    /// <summary>PSVita package key, revision 3 (key_type 3).</summary>
    public static readonly byte[] VitaPkgAesKey3 = Hex("423ACA3A2BD5649F9686ABAD6FD8801F");

    /// <summary>PSVita package key, revision 4 (key_type 4).</summary>
    public static readonly byte[] VitaPkgAesKey4 = Hex("AF07FD59652527BAF13389668B17D9EA");
}
