using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Utilities;

namespace PkgLens.Core.Ps3.Self;

/// <summary>A legacy CEX SELF header signing profile, not a console compatibility guarantee.</summary>
public sealed record LegacySelfProfile(uint ProgramType, ushort Revision, byte CurveType)
{
    public string Label => $"Legacy CEX {(ProgramType == 8 ? "NPDRM" : "APP")} — key {Revision:X2}";
    public override string ToString() => Label;
}

/// <summary>
/// Published legacy APP/NPDRM signing keys, cross-checked against RPCS3's key vault.
/// Signatures use the native SELF 21-byte r + 21-byte s representation, not DER.
/// This does not sign PKGs or NPDRM footers, nor provide modern or DEX signing keys.
/// </summary>
public static class LegacySelfSigning
{
    private sealed record Material(LegacySelfProfile Profile, ECPrivateKeyParameters Private, ECPublicKeyParameters Public);
    private static readonly Lazy<IReadOnlyDictionary<(uint, ushort), Material>> Materials = new(Load);

    public static IReadOnlyList<LegacySelfProfile> Profiles(bool npdrm) => Materials.Value.Values
        .Where(m => m.Profile.ProgramType == (npdrm ? 8u : 4u)).Select(m => m.Profile)
        .OrderBy(m => m.Revision).ToArray();

    public static bool IsSupported(uint programType, ushort revision) => Materials.Value.ContainsKey((programType, revision));

    internal static byte[] SignHash(uint programType, ushort revision, byte[] hash)
    {
        if (hash.Length != 20) throw new ArgumentException("SELF signatures require a SHA-1 digest.", nameof(hash));
        Material material = Require(programType, revision);
        // RFC 6979 avoids dependence on per-signature random nonce quality; file encryption still
        // receives fresh CSPRNG keys/IVs, so rebuilding the same ELF produces different SELF bytes.
        var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(true, material.Private);
        BigInteger[] values = signer.GenerateSignature(hash);
        byte[] signature = new byte[42];
        BigIntegers.AsUnsignedByteArray(21, values[0]).CopyTo(signature, 0);
        BigIntegers.AsUnsignedByteArray(21, values[1]).CopyTo(signature, 21);
        if (!VerifyHash(programType, revision, hash, signature))
            throw new CryptographicException("Generated SELF signature failed verification.");
        return signature;
    }

    internal static bool VerifyHash(uint programType, ushort revision, byte[] hash, ReadOnlySpan<byte> signature)
    {
        if (hash.Length != 20 || signature.Length != 42) return false;
        var verifier = new ECDsaSigner();
        verifier.Init(false, Require(programType, revision).Public);
        return verifier.VerifySignature(hash, new BigInteger(1, signature[..21].ToArray()),
            new BigInteger(1, signature[21..].ToArray()));
    }

    private static Material Require(uint programType, ushort revision) =>
        Materials.Value.TryGetValue((programType, revision), out var material) ? material :
            throw new PkgFormatException($"No verified legacy signing profile for {(programType == 8 ? "NPDRM" : "APP")} key {revision:X2}. Choose a supported legacy profile, or turn signing off.");

    private static IReadOnlyDictionary<(uint, ushort), Material> Load()
    {
        using var stream = typeof(LegacySelfSigning).Assembly.GetManifestResourceStream("PkgLens.Core.legacy-signing-profiles.json")
            ?? throw new InvalidOperationException("Bundled SELF signing profiles are missing.");
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<(uint, ushort), Material>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            string Text(string name) => entry.GetProperty(name).GetString()!;
            BigInteger Number(string name) => new(Text(name), 16);
            var profile = new LegacySelfProfile(entry.GetProperty("ProgramType").GetUInt32(),
                entry.GetProperty("Revision").GetUInt16(), entry.GetProperty("CurveType").GetByte());
            SelfKey? root = SelfKeyset.Find(profile.ProgramType, profile.Revision);
            if (root is null || !root.Erk.AsSpan().SequenceEqual(Convert.FromHexString(Text("Erk"))) ||
                !root.Riv.AsSpan().SequenceEqual(Convert.FromHexString(Text("Riv"))))
                throw new CryptographicException("Legacy signing profile does not match the bundled encryption key.");
            var order = Number("N");
            var curve = new FpCurve(Number("P"), Number("A"), Number("B"), order, BigInteger.One);
            var generator = curve.ValidatePoint(Number("Gx"), Number("Gy"));
            var domain = new ECDomainParameters(curve, generator, order, BigInteger.One);
            byte[] pub = Convert.FromHexString(Text("PublicKey"));
            if (pub.Length != 40) throw new CryptographicException("Invalid legacy SELF public-key length.");
            var point = curve.ValidatePoint(new BigInteger(1, pub[..20]), new BigInteger(1, pub[20..]));
            var secret = Number("PrivateKey");
            if (secret.SignValue <= 0 || secret.CompareTo(order) >= 0 ||
                !generator.Multiply(order).IsInfinity || !point.Multiply(order).IsInfinity ||
                !generator.Multiply(secret).Normalize().Equals(point.Normalize()))
                throw new CryptographicException("Legacy SELF signing key or curve failed validation.");
            result.Add((profile.ProgramType, profile.Revision), new Material(profile,
                new ECPrivateKeyParameters(secret, domain), new ECPublicKeyParameters(point, domain)));
        }
        return result;
    }
}
