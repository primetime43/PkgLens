using System.Security.Cryptography;

namespace PkgLens.Core.Shared.Crypto;

/// <summary>
/// AES-CMAC (RFC 4493), built on AES-128 ECB. The .NET BCL has no CMAC, so this is a small,
/// self-contained implementation. Used to verify a PKG's header CMAC.
/// </summary>
public static class AesCmac
{
    public static byte[] Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        using var accumulator = new AesCmacAccumulator(key);
        accumulator.Append(message);
        return accumulator.FinalizeHash();
    }

    /// <summary>Left-shift by one bit over 128 bits, conditionally XOR the Rb constant (0x87).</summary>
    internal static void Dbl(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int carry = 0;
        for (int i = 15; i >= 0; i--)
        {
            int v = (input[i] << 1) | carry;
            output[i] = (byte)v;
            carry = (input[i] & 0x80) != 0 ? 1 : 0;
        }
        if ((input[0] & 0x80) != 0)
            output[15] ^= 0x87;
    }

    internal static void Xor(Span<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < 16; i++) a[i] ^= b[i];
    }
}

internal sealed class AesCmacAccumulator : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _k1 = new byte[16];
    private readonly byte[] _k2 = new byte[16];
    private readonly byte[] _state = new byte[16];
    private readonly byte[] _pending = new byte[16];
    private int _pendingLength;
    private bool _finalized;

    public AesCmacAccumulator(ReadOnlySpan<byte> key)
    {
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key.ToArray();

        Span<byte> zero = stackalloc byte[16];
        Span<byte> encryptedZero = stackalloc byte[16];
        _aes.EncryptEcb(zero, encryptedZero, PaddingMode.None);
        AesCmac.Dbl(encryptedZero, _k1);
        AesCmac.Dbl(_k1, _k2);
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_finalized, this);

        while (!data.IsEmpty)
        {
            if (_pendingLength == 16)
            {
                ProcessBlock(_pending);
                _pendingLength = 0;
            }

            int count = Math.Min(16 - _pendingLength, data.Length);
            data[..count].CopyTo(_pending.AsSpan(_pendingLength));
            _pendingLength += count;
            data = data[count..];
        }
    }

    public byte[] FinalizeHash()
    {
        ObjectDisposedException.ThrowIf(_finalized, this);
        _finalized = true;

        Span<byte> last = stackalloc byte[16];
        _pending.AsSpan(0, _pendingLength).CopyTo(last);
        if (_pendingLength == 16)
        {
            AesCmac.Xor(last, _k1);
        }
        else
        {
            last[_pendingLength] = 0x80;
            AesCmac.Xor(last, _k2);
        }

        AesCmac.Xor(last, _state);
        var hash = new byte[16];
        _aes.EncryptEcb(last, hash, PaddingMode.None);
        return hash;
    }

    private void ProcessBlock(ReadOnlySpan<byte> block)
    {
        Span<byte> input = stackalloc byte[16];
        block.CopyTo(input);
        AesCmac.Xor(input, _state);
        _aes.EncryptEcb(input, _state, PaddingMode.None);
    }

    public void Dispose()
    {
        _finalized = true;
        _aes.Dispose();
    }
}
