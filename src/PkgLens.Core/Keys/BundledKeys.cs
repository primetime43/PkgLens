namespace PkgLens.Core.Keys;

/// <summary>
/// Public PS3 package-decryption keys shipped with PkgLens. These are the <b>NPDRM PKG AES keys</b> —
/// universal, symmetric <em>decryption</em> constants published for well over a decade and embedded by
/// every PS3 package tool (RPCS3, scetool, make_npdata, …). They are the same category as the SELF
/// keysets (<see cref="Self.SelfKeyset"/>) and EDAT keys (<see cref="Npd.NpdKeys"/>) already bundled:
/// none is a private/signing key, a per-console secret (IDPS/EID), or a per-purchase license (RAP).
/// Bundling them lets retail packages decrypt out of the box, so the user only ever supplies material
/// that is genuinely theirs (a RAP for a licensed EDAT/EBOOT).
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
}
