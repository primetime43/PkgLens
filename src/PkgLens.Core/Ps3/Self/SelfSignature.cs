using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PkgLens.Core.Ps3.Self;

public enum SelfSignatureStatus { Valid, Invalid, Absent, UnsupportedProfile }

/// <summary>Verifies the encrypted SELF header's ECDSA signature with a bundled legacy public key.</summary>
public static class SelfSignature
{
    /// <summary>
    /// Decrypts metadata, validates its bounds, and verifies the header signature. Does not verify
    /// payload hashes, NPDRM footer signatures, entitlements, or console compatibility.
    /// Invalid/truncated metadata and incorrect content keys throw <see cref="PkgFormatException"/>.
    /// </summary>
    public static SelfSignatureStatus VerifyHeader(byte[] self, byte[]? klicensee = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        SelfInfo info = SelfReader.ParseInfo(new MemoryStream(self, writable: false));
        if (info.KeyRevision is 0x8000 or 0xC000) return SelfSignatureStatus.Absent;
        if (!LegacySelfSigning.IsSupported(info.RawProgramType, info.KeyRevision))
            return SelfSignatureStatus.UnsupportedProfile;
        ulong metadataInfo = (ulong)info.MetadataOffset + 32;
        ulong metadata = metadataInfo + 64;
        if (info.HeaderLength > (ulong)self.Length || info.HeaderLength > 32 * 1024 * 1024 ||
            metadataInfo < 0x70 || metadata + 32 > info.HeaderLength)
            throw new PkgFormatException("SELF signature metadata is out of bounds.");
        byte[] header = self.AsSpan(0, (int)info.HeaderLength).ToArray();
        int mi = (int)metadataInfo, mh = (int)metadata;
        byte[] plaintextInfo = header.AsSpan(mi, 64).ToArray();
        using var aes = Aes.Create();
        if (info.RawProgramType == 8)
        {
            if (info.Npdrm is null) throw new PkgFormatException("NPDRM control block is missing.");
            byte[]? key = klicensee ?? (info.Npdrm.RawLicenseType == 3 ? SelfKeyset.NpKlicFree : null);
            if (key is not { Length: 16 })
                throw new PkgFormatException("A 16-byte NPDRM klicensee is required to check this signature.");
            aes.Key = SelfKeyset.NpKlicKey;
            aes.Key = aes.DecryptEcb(key, PaddingMode.None);
            plaintextInfo = aes.DecryptCbc(plaintextInfo, new byte[16], PaddingMode.None);
        }
        var root = SelfKeyset.Find(info.RawProgramType, info.KeyRevision)!;
        aes.Key = root.Erk;
        plaintextInfo = aes.DecryptCbc(plaintextInfo, root.Riv, PaddingMode.None);
        if (plaintextInfo.AsSpan(16, 16).IndexOfAnyExcept((byte)0) >= 0 ||
            plaintextInfo.AsSpan(48, 16).IndexOfAnyExcept((byte)0) >= 0)
            throw new PkgFormatException("Cannot verify SELF signature: metadata key or klicensee is incorrect.");
        plaintextInfo.CopyTo(header, mi);
        EncryptedSelfBuilder.Ctr(plaintextInfo.AsSpan(0, 16), plaintextInfo.AsSpan(32, 16), header.AsSpan(mh));
        ulong signatureOffset = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(mh));
        ulong sections = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(mh + 12));
        ulong keys = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(mh + 16));
        ulong optional = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(mh + 20));
        ulong tablesEnd = metadata + 32 + sections * 48 + keys * 16 + optional;
        if (signatureOffset < tablesEnd || signatureOffset > info.HeaderLength ||
            info.HeaderLength - signatureOffset < 48)
            throw new PkgFormatException("SELF signature offset or metadata tables are out of bounds.");
        var signature = header.AsSpan((int)signatureOffset, 42);
        if (signature.IndexOfAnyExcept((byte)0) < 0) return SelfSignatureStatus.Absent;
        byte[] hash = SHA1.HashData(header.AsSpan(0, (int)signatureOffset));
        return LegacySelfSigning.VerifyHash(info.RawProgramType, info.KeyRevision, hash, signature)
            ? SelfSignatureStatus.Valid : SelfSignatureStatus.Invalid;
    }
}
