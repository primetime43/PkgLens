using System.Security.Cryptography;

namespace PkgLens.Core.Crypto;

/// <summary>
/// AES-CMAC (RFC 4493), built on AES-128 ECB. The .NET BCL has no CMAC, so this is a small,
/// self-contained implementation. Used to verify a PKG's header CMAC.
/// </summary>
public static class AesCmac
{
    public static byte[] Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();

        // Subkeys K1, K2 from L = AES(key, 0^16).
        Span<byte> zero = stackalloc byte[16];
        Span<byte> l = stackalloc byte[16];
        aes.EncryptEcb(zero, l, PaddingMode.None);
        Span<byte> k1 = stackalloc byte[16];
        Span<byte> k2 = stackalloc byte[16];
        Dbl(l, k1);
        Dbl(k1, k2);

        int n = message.Length == 0 ? 1 : (message.Length + 15) / 16;
        bool lastComplete = message.Length > 0 && message.Length % 16 == 0;

        Span<byte> mLast = stackalloc byte[16];
        int lastStart = (n - 1) * 16;
        if (lastComplete)
        {
            message.Slice(lastStart, 16).CopyTo(mLast);
            Xor(mLast, k1);
        }
        else
        {
            int rem = message.Length - lastStart;
            mLast.Clear();
            message.Slice(lastStart, rem).CopyTo(mLast);
            mLast[rem] = 0x80; // padding
            Xor(mLast, k2);
        }

        Span<byte> x = stackalloc byte[16]; // running state, starts at 0
        Span<byte> y = stackalloc byte[16];
        for (int i = 0; i < n - 1; i++)
        {
            message.Slice(i * 16, 16).CopyTo(y);
            Xor(y, x);
            aes.EncryptEcb(y, x, PaddingMode.None);
        }

        Xor(mLast, x);
        var tag = new byte[16];
        aes.EncryptEcb(mLast, tag, PaddingMode.None);
        return tag;
    }

    /// <summary>Left-shift by one bit over 128 bits, conditionally XOR the Rb constant (0x87).</summary>
    private static void Dbl(ReadOnlySpan<byte> input, Span<byte> output)
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

    private static void Xor(Span<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < 16; i++) a[i] ^= b[i];
    }
}
