using System.Buffers.Binary;

namespace PkgLens.Core.Psp;

/// <summary>
/// Decrypts a PSP PGD (PlayStation Game Data) blob in place, using <see cref="PspAmctrl"/>. Ported
/// from tpunix's <c>pgd_open</c>/<c>pgd_decrypt</c>. Only fixed-key PGDs (<c>drm_type == 1</c>) are
/// supported; fuse-bound ones (<c>drm_type == 2</c>) need the originating console's per-console key
/// and are rejected. The AMCTRL BBMac checks make a successful decrypt self-verifying.
/// </summary>
internal static class PspPgd
{
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    /// <summary>
    /// Decrypts the PGD at the start of <paramref name="pgd"/> in place. Returns the plaintext length;
    /// the decrypted data lands at offset 0x90. <paramref name="pgdFlag"/> selects the fixed key
    /// (2 = EDAT). Throws <see cref="PkgFormatException"/> on a MAC failure (wrong data / unsupported).
    /// </summary>
    public static int Decrypt(byte[] pgd, int pgdSize, int pgdFlag)
    {
        var amctrl = new PspAmctrl();

        uint keyIndex = U32(pgd, 4);
        uint drmType = U32(pgd, 8);

        int macType, cipherType;
        if (drmType == 1)
        {
            macType = 1;
            pgdFlag |= 4;
            if (keyIndex > 1) { macType = 3; pgdFlag |= 8; }
            cipherType = 1;
        }
        else
        {
            throw new PkgFormatException(
                $"PSP PGD drm_type {drmType} is fuse-bound (per-console key) and cannot be decrypted; only fixed-key (drm_type 1) is supported.");
        }

        // Fixed-key selection matches the reference: flag&2 → 1A90, flag&1 → 1AA0 (the latter wins).
        byte[]? fkey = null;
        if ((pgdFlag & 2) != 0) fkey = PspKirkKeys.DnasKey1A90;
        if ((pgdFlag & 1) != 0) fkey = PspKirkKeys.DnasKey1AA0;
        if (fkey is null)
            throw new PkgFormatException($"PSP PGD: no fixed key for flag 0x{pgdFlag:X}.");

        // MAC over the first 0x80 bytes — verifies the header with the fixed key.
        var mkey = new MacKey();
        amctrl.BBMacInit(mkey, macType);
        amctrl.BBMacUpdate(mkey, pgd, 0, 0x80);
        if (!amctrl.BBMacFinal2(mkey, pgd, 0x80, fkey))
            throw new PkgFormatException("PSP PGD header MAC check failed (corrupt file or unsupported DRM).");

        // MAC over the first 0x70 bytes — recovers the version key.
        var vkey = new byte[16];
        amctrl.BBMacInit(mkey, macType);
        amctrl.BBMacUpdate(mkey, pgd, 0, 0x70);
        amctrl.BBMacGetKey(mkey, pgd, 0x70, vkey);

        // Decrypt the 0x30-byte PGD descriptor at 0x30 (reveals sizes/offsets), keyed by bytes at 0x10.
        var ckey = new CipherKey();
        amctrl.BBCipherInit(ckey, cipherType, pgd, 0x10, vkey, 0);
        amctrl.BBCipherUpdate(ckey, pgd, 0x30, 0x30);

        int dataSize = (int)U32(pgd, 0x44);
        int blockSize = (int)U32(pgd, 0x48);
        int dataOffset = (int)U32(pgd, 0x4C);

        if (dataSize < 0 || blockSize <= 0 || dataOffset < 0)
            throw new PkgFormatException("PSP PGD: implausible descriptor (wrong key?).");

        int alignSize = (dataSize + 15) & ~15;
        int tableOffset = dataOffset + alignSize;
        int blockNr = ((alignSize + blockSize - 1) & ~(blockSize - 1)) / blockSize;

        if ((long)tableOffset + (long)blockNr * 16 > pgdSize)
            throw new PkgFormatException("PSP PGD: data/table extends past the file (wrong key or corrupt).");

        // Verify the block-hash table.
        amctrl.BBMacInit(mkey, macType);
        amctrl.BBMacUpdate(mkey, pgd, tableOffset, blockNr * 16);
        if (!amctrl.BBMacFinal2(mkey, pgd, 0x60, vkey))
            throw new PkgFormatException("PSP PGD table MAC check failed.");

        // Decrypt the data, keyed by the (now plaintext) descriptor at 0x30.
        amctrl.BBCipherInit(ckey, cipherType, pgd, 0x30, vkey, 0);
        amctrl.BBCipherUpdate(ckey, pgd, 0x90, alignSize);

        return dataSize;
    }
}
