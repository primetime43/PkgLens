using System.Security.Cryptography;

namespace PkgLens.Core.Ps3;

/// <summary>The outcome of checking a PKG header's ECDSA signature.</summary>
public enum PkgSignatureResult
{
    /// <summary>Signature present and verifies against Sony's public NPDRM key (a genuine retail signature).</summary>
    Valid,
    /// <summary>Signature present but does not verify — repacked, fake-signed, or altered.</summary>
    Invalid,
    /// <summary>No signature present (all-zero) — a fake-signed / homebrew package leaves this blank.</summary>
    Absent,
    /// <summary>The platform's crypto backend could not construct the explicit curve, so no check ran.</summary>
    Unsupported,
}

/// <summary>
/// Verifies the ECDSA signature in a PS3 / PSP NPDRM PKG header (the "NpDrm Signature" in the 0x40
/// digest block). <b>Verification only</b> — the bundled key is Sony's <em>public</em> NPDRM key,
/// which cannot sign anything, so nothing here forges or re-signs a package.
///
/// The signature at <c>header[0x90:0xB8]</c> (<c>r || s</c>, 20 + 20 bytes on a 160-bit curve) is an
/// ECDSA signature over <c>SHA-1(header[0x00:0x80])</c> — the same region the truncated SHA-1 at 0xB8
/// covers. The curve and public key are the published PS3 "VSH" ECDSA curve #2 and NPDRM public key
/// (per PSN_get_pkg_info / psdevwiki), confirmed byte-for-byte against real retail PS3 and PSP packages.
/// </summary>
public static class NpdrmSignature
{
    // Published PS3 "VSH" ECDSA curve #2 — a 160-bit prime short-Weierstrass curve. All public.
    private static readonly byte[] Prime    = Convert.FromHexString("ffffffffffffffff00000001ffffffffffffffff");
    private static readonly byte[] CurveA   = Convert.FromHexString("ffffffffffffffff00000001fffffffffffffffc");
    private static readonly byte[] CurveB   = Convert.FromHexString("a68bedc33418029c1d3ce33b9a321fccbb9e0f0b");
    private static readonly byte[] Gx       = Convert.FromHexString("128ec4256487fd8fdf64e2437bc0a1f6d5afde2c");
    private static readonly byte[] Gy       = Convert.FromHexString("5958557eb1db001260425524dbc379d5ac5f4adf");
    private static readonly byte[] Order    = Convert.FromHexString("fffffffffffffffeffffb5ae3c523e63944f2127");
    private static readonly byte[] Cofactor = { 0x01 };

    // Published PS3 "VSH NPDRM" public-key point on curve #2. A verification key only — cannot sign.
    private static readonly byte[] Qx = Convert.FromHexString("e6792e446ceba27bcadf374b99504fd8e80adfeb");
    private static readonly byte[] Qy = Convert.FromHexString("3e66de73ffe58d3291221c65018c038d3822c3c9");

    /// <summary>The header region covered by the signature hash: SHA-1(header[0x00:0x80]).</summary>
    private const int SignedRegion = 0x80;
    private const int SignatureOffset = 0x90;
    private const int SignatureLength = 0x28; // r (0x14) || s (0x14)

    /// <summary>Minimum header bytes needed to check the signature (through the end of the signature field).</summary>
    public const int RequiredLength = SignatureOffset + SignatureLength; // 0xB8

    /// <summary>
    /// Verifies the NPDRM header signature. <paramref name="header"/> must be at least
    /// <see cref="RequiredLength"/> bytes (the start of the file through 0xB8).
    /// </summary>
    public static PkgSignatureResult VerifyHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < RequiredLength)
            return PkgSignatureResult.Absent;

        var signature = header.Slice(SignatureOffset, SignatureLength);
        if (AllZero(signature))
            return PkgSignatureResult.Absent;

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(header[..SignedRegion], hash);

        try
        {
            var parameters = new ECParameters
            {
                Curve = new ECCurve
                {
                    CurveType = ECCurve.ECCurveType.PrimeShortWeierstrass,
                    Prime = Prime,
                    A = CurveA,
                    B = CurveB,
                    G = new ECPoint { X = Gx, Y = Gy },
                    Order = Order,
                    Cofactor = Cofactor,
                },
                Q = new ECPoint { X = Qx, Y = Qy },
            };
            using var ecdsa = ECDsa.Create(parameters);
            return ecdsa.VerifyHash(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? PkgSignatureResult.Valid
                : PkgSignatureResult.Invalid;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or ArgumentException)
        {
            // Some crypto backends reject arbitrary explicit prime curves — degrade to "not checked"
            // rather than failing the whole verify.
            return PkgSignatureResult.Unsupported;
        }
    }

    private static bool AllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            if (b != 0) return false;
        return true;
    }
}
