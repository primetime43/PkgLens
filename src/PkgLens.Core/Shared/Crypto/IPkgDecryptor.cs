namespace PkgLens.Core.Shared.Crypto;

/// <summary>
/// Decrypts regions of a PKG's encrypted data area. Both the retail (AES-128-CTR) and debug
/// (SHA-1 keystream) schemes are stream ciphers keyed by absolute 16-byte block index within
/// the data region, so a single call can start at any byte offset.
/// </summary>
public interface IPkgDecryptor
{
    /// <summary>
    /// Decrypts <paramref name="data"/> in place. <paramref name="dataRegionOffset"/> is the
    /// position of <c>data[0]</c> relative to <c>header.data_offset</c> (i.e. the same offsets
    /// stored in item records). Any starting offset is supported; the keystream is derived from
    /// the containing 16-byte blocks.
    /// </summary>
    void DecryptInPlace(Span<byte> data, long dataRegionOffset);
}
