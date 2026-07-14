using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PkgLens.Core.Npd.Psp;

/// <summary>A running BBMac (AES-CMAC-like) state — the AMCTRL <c>MAC_KEY</c>.</summary>
internal sealed class MacKey
{
    public int Type;
    public readonly byte[] Key = new byte[16];
    public readonly byte[] Pad = new byte[16];
    public int PadSize;
}

/// <summary>A BBCipher keystream state — the AMCTRL <c>CIPHER_KEY</c>.</summary>
internal sealed class CipherKey
{
    public int Type;
    public uint Seed;
    public readonly byte[] Key = new byte[16];
}

/// <summary>
/// PSP AMCTRL (<c>amctrl.prx</c>) reimplemented over the KIRK CBC primitives — the BBMac and BBCipher
/// used to decrypt a PGD. Ported faithfully from tpunix's <c>amctrl.c</c> (KIRK CMD4/CMD7 are just
/// AES-128-CBC with a fixed zero IV and a keyed table lookup). Only the fixed-key path is supported
/// (mac/cipher type 1 and 3); the fuse-id path (type 2) needs a per-console secret and is rejected.
/// One instance owns the shared 0x814-byte KIRK work buffer, so it is not thread-safe.
/// </summary>
internal sealed class PspAmctrl
{
    private const int KOff = 0x14; // data sits at kirk_buf + 0x14
    private static readonly byte[] Zero16 = new byte[16];

    private readonly byte[] _kirk = new byte[0x814];

    // ---- KIRK CMD4 (AES-CBC encrypt) / CMD7 (AES-CBC decrypt), IV = 0, key by keyseed ----

    private void Kirk4(int size, int keyseed)
    {
        using var aes = Aes.Create();
        aes.Key = PspKirkKeys.Kirk7(keyseed);
        var ct = aes.EncryptCbc(_kirk.AsSpan(KOff, size), Zero16, PaddingMode.None);
        ct.CopyTo(_kirk.AsSpan(KOff));
    }

    private void Kirk7(int size, int keyseed)
    {
        using var aes = Aes.Create();
        aes.Key = PspKirkKeys.Kirk7(keyseed);
        var pt = aes.DecryptCbc(_kirk.AsSpan(KOff, size), Zero16, PaddingMode.None);
        pt.CopyTo(_kirk.AsSpan(0));
    }

    // ---- BBMac ----

    private void Sub158(int size, byte[] key, int keyType)
    {
        for (int i = 0; i < 16; i++) _kirk[KOff + i] ^= key[i];
        Kirk4(size, keyType);
        Array.Copy(_kirk, size + 4, key, 0, 16); // last 16 bytes of the encrypted block
    }

    public void BBMacInit(MacKey mkey, int type)
    {
        mkey.Type = type;
        mkey.PadSize = 0;
        Array.Clear(mkey.Key);
        Array.Clear(mkey.Pad);
    }

    public void BBMacUpdate(MacKey mkey, byte[] buf, int off, int size)
    {
        if (mkey.PadSize > 16) throw new PkgFormatException("PSP EDAT: BBMac pad overflow.");

        if (mkey.PadSize + size <= 16)
        {
            Array.Copy(buf, off, mkey.Pad, mkey.PadSize, size);
            mkey.PadSize += size;
            return;
        }

        Array.Copy(mkey.Pad, 0, _kirk, KOff, mkey.PadSize);
        int p = mkey.PadSize;

        mkey.PadSize = (mkey.PadSize + size) & 0x0f;
        if (mkey.PadSize == 0) mkey.PadSize = 16;

        size -= mkey.PadSize;
        Array.Copy(buf, off + size, mkey.Pad, 0, mkey.PadSize);

        int type = mkey.Type == 2 ? 0x3A : 0x38;
        while (size != 0)
        {
            int ksize = size + p >= 0x0800 ? 0x0800 : size + p;
            Array.Copy(buf, off, _kirk, KOff + p, ksize - p);
            Sub158(ksize, mkey.Key, type);
            size -= ksize - p;
            off += ksize - p;
            p = 0;
        }
    }

    /// <summary>Left-shifts a 16-byte block one bit (with the 0x87 GF reduction), in place.</summary>
    private static void ShiftLeft1(byte[] t)
    {
        int t0 = (t[0] & 0x80) != 0 ? 0x87 : 0;
        for (int i = 0; i < 15; i++)
            t[i] = (byte)((t[i] << 1) | (t[i + 1] >> 7));
        t[15] = (byte)((t[15] << 1) ^ t0);
    }

    public void BBMacFinal(MacKey mkey, byte[] outMac, byte[]? vkey)
    {
        if (mkey.PadSize > 16) throw new PkgFormatException("PSP EDAT: BBMac pad overflow.");
        int code = mkey.Type == 2 ? 0x3A : 0x38;

        Array.Clear(_kirk, KOff, 16);
        Kirk4(16, code);
        var tmp = new byte[16];
        Array.Copy(_kirk, KOff, tmp, 0, 16);

        ShiftLeft1(tmp);
        if (mkey.PadSize < 16)
        {
            ShiftLeft1(tmp);
            mkey.Pad[mkey.PadSize] = 0x80;
            if (mkey.PadSize + 1 < 16)
                Array.Clear(mkey.Pad, mkey.PadSize + 1, 16 - mkey.PadSize - 1);
        }

        for (int i = 0; i < 16; i++) mkey.Pad[i] ^= tmp[i];

        Array.Copy(mkey.Pad, 0, _kirk, KOff, 16);
        var tmp1 = new byte[16];
        Array.Copy(mkey.Key, tmp1, 16);
        Sub158(0x10, tmp1, code);

        for (int i = 0; i < 16; i++) tmp1[i] ^= PspKirkKeys.Amctrl1CD4[i];

        // (type == 2 fuse path intentionally omitted — unsupported.)

        if (vkey is not null)
        {
            for (int i = 0; i < 16; i++) tmp1[i] ^= vkey[i];
            Array.Copy(tmp1, 0, _kirk, KOff, 16);
            Kirk4(0x10, code);
            Array.Copy(_kirk, KOff, tmp1, 0, 16);
        }

        Array.Copy(tmp1, outMac, 16);

        Array.Clear(mkey.Key);
        Array.Clear(mkey.Pad);
        mkey.PadSize = 0;
        mkey.Type = 0;
    }

    /// <summary>Verifies a stored BBMac. Returns true when it matches (correct key + intact data).</summary>
    public bool BBMacFinal2(MacKey mkey, byte[] buf, int macOff, byte[]? vkey)
    {
        int type = mkey.Type;
        var tmp = new byte[16];
        BBMacFinal(mkey, tmp, vkey);

        if (type == 3)
        {
            Array.Copy(buf, macOff, _kirk, KOff, 16);
            Kirk7(16, 0x63);
        }
        else
        {
            Array.Copy(buf, macOff, _kirk, 0, 16);
        }

        for (int i = 0; i < 16; i++)
            if (_kirk[i] != tmp[i]) return false;
        return true;
    }

    /// <summary>Recovers the version key from the MAC at <paramref name="macOff"/>.</summary>
    public void BBMacGetKey(MacKey mkey, byte[] buf, int macOff, byte[] vkeyOut)
    {
        int type = mkey.Type;
        var tmp = new byte[16];
        BBMacFinal(mkey, tmp, null);

        if (type == 3)
        {
            Array.Copy(buf, macOff, _kirk, KOff, 16);
            Kirk7(16, 0x63);
        }
        else
        {
            Array.Copy(buf, macOff, _kirk, 0, 16);
        }

        var tmp1 = new byte[16];
        Array.Copy(_kirk, 0, tmp1, 0, 16);
        Array.Copy(tmp1, 0, _kirk, KOff, 16);

        int code = type == 2 ? 0x3A : 0x38;
        Kirk7(16, code);

        for (int i = 0; i < 16; i++) vkeyOut[i] = (byte)(tmp[i] ^ _kirk[i]);
    }

    // ---- BBCipher ----

    private void Sub1F8(int size, byte[] key, int keyType)
    {
        var tmp = new byte[16];
        Array.Copy(_kirk, size + 4, tmp, 0, 16); // last 16 of the input ciphertext
        Kirk7(size, keyType);
        for (int i = 0; i < 16; i++) _kirk[i] ^= key[i];
        Array.Copy(tmp, key, 16);
    }

    private void Sub428(byte[] dbuf, int dOff, int size, CipherKey ckey)
    {
        Array.Copy(ckey.Key, 0, _kirk, KOff, 16);
        for (int i = 0; i < 16; i++) _kirk[KOff + i] ^= PspKirkKeys.Amctrl1CF4[i];

        if (ckey.Type == 2)
            throw new PkgFormatException("PSP EDAT: fuse-id cipher (type 2) needs a per-console key; not supported.");
        Kirk7(16, 0x39);

        for (int i = 0; i < 16; i++) _kirk[i] ^= PspKirkKeys.Amctrl1CE4[i];

        var tmp2 = new byte[16];
        Array.Copy(_kirk, 0, tmp2, 0, 16);

        var tmp1 = new byte[16];
        if (ckey.Seed == 1)
        {
            Array.Clear(tmp1);
        }
        else
        {
            Array.Copy(tmp2, tmp1, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(tmp1.AsSpan(0x0C), ckey.Seed - 1);
        }

        for (int i = 0; i < size; i += 16)
        {
            Array.Copy(tmp2, 0, _kirk, KOff + i, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(_kirk.AsSpan(KOff + i + 12), ckey.Seed);
            ckey.Seed += 1;
        }

        Sub1F8(size, tmp1, 0x63);

        for (int i = 0; i < size; i++) dbuf[dOff + i] ^= _kirk[i];
    }

    /// <summary>Initializes a decrypt-mode BBCipher (mode 2): key = header_key ^ version_key.</summary>
    public void BBCipherInit(CipherKey ckey, int type, byte[] headerKey, int headerOff, byte[] versionKey, uint seed)
    {
        ckey.Type = type;
        ckey.Seed = seed + 1;
        for (int i = 0; i < 16; i++) ckey.Key[i] = headerKey[headerOff + i];
        if (versionKey is not null)
            for (int i = 0; i < 16; i++) ckey.Key[i] ^= versionKey[i];
    }

    public void BBCipherUpdate(CipherKey ckey, byte[] data, int off, int size)
    {
        int p = 0;
        while (size > 0)
        {
            int dsize = size >= 0x0800 ? 0x0800 : size;
            Sub428(data, off + p, dsize, ckey);
            size -= dsize;
            p += dsize;
        }
    }
}
