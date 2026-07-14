using System.Security.Cryptography;

namespace PkgLens.Core.Shared.Crypto;

/// <summary>
/// Keystream for finalized (retail) packages: AES-128 in counter (CTR) mode. .NET has no
/// built-in CTR, so it is composed from AES-ECB — each keystream block is
/// <c>AES-ECB-Encrypt(counter)</c> where <c>counter = data_riv + blockIndex</c> as a
/// big-endian 128-bit integer (verified against RPCS3's <c>unpkg.cpp</c>).
///
/// The 16-byte AES key is <b>never bundled</b>: it is supplied at runtime through
/// <see cref="Keys.IKeyProvider"/> from a user key file.
/// </summary>
public sealed class RetailAesCtrDecryptor : BlockKeystreamDecryptor, IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _riv;

    public RetailAesCtrDecryptor(ReadOnlySpan<byte> aesKey, ReadOnlySpan<byte> dataRiv)
    {
        if (aesKey.Length != 16)
            throw new ArgumentException("PS3 gpkg AES key must be 16 bytes.", nameof(aesKey));
        if (dataRiv.Length != 16)
            throw new ArgumentException("data_riv must be 16 bytes.", nameof(dataRiv));

        _riv = dataRiv.ToArray();
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = aesKey.ToArray();
    }

    protected override void FillBlock(ulong blockIndex, Span<byte> block)
    {
        Span<byte> counter = stackalloc byte[16];
        _riv.CopyTo(counter);
        AddBigEndian(counter, blockIndex);
        _aes.EncryptEcb(counter, block, PaddingMode.None);
    }

    /// <summary>Adds <paramref name="value"/> to a 16-byte big-endian integer, in place, with carry.</summary>
    internal static void AddBigEndian(Span<byte> counter, ulong value)
    {
        int i = counter.Length - 1;
        ulong carry = value;
        while (carry != 0 && i >= 0)
        {
            ulong sum = counter[i] + (carry & 0xFF);
            counter[i] = (byte)sum;
            carry = (carry >> 8) + (sum >> 8);
            i--;
        }
    }

    public void Dispose() => _aes.Dispose();
}
