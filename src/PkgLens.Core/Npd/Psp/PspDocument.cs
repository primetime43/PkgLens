using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PkgLens.Core.Npd.Psp;

/// <summary>
/// Decrypts a PSP / PS-minis <c>DOCUMENT.DAT</c> — the in-game software manual — into its PNG pages.
/// The file is a DES-CBC container of PNG images keyed by a per-document key: when a sibling
/// <c>DOCINFO.EDAT</c> is present, its decrypted 8-byte payload (recovered via <see cref="PspEdatFile"/>)
/// XORed with a fixed constant is the DES key; otherwise a fixed default key is used. Integrity is
/// carried by SHA-1 HMACs (fixed public keys) over the header, metadata, and each page. Faithful port
/// of seiya-dev/PSP-DOCUMENT.DAT's <c>decrypt_document_psp</c>; verified against a real minis manual.
/// All keys are public; nothing here signs or re-encrypts.
/// </summary>
public static class PspDocument
{
    private static readonly byte[] HmacKeyPsp = Convert.FromHexString("4D1B6B1269DDD22FAAE1F54207E798B5");
    private static readonly byte[] HmacKeyPs3 = Convert.FromHexString("EF690EC0E0BFA41F08455BD038EB8762");
    private static readonly byte[] DefaultDesKey = Convert.FromHexString("DA3923EF9C61B930");
    private static readonly byte[] DesIv = Convert.FromHexString("2DEE8950969112D9");
    private static readonly byte[] DocKeyXor = Convert.FromHexString("F932FF26474A8DC0");

    /// <summary>The fixed 16-byte "dummy PGD" header every DOCUMENT.DAT begins with.</summary>
    private static readonly byte[] DocHeader = Convert.FromHexString("00504744010000000100000000000000");
    private static readonly byte[] DocMagic = "DOC "u8.ToArray();
    private static readonly byte[] DocVersion = { 0, 0, 1, 0, 0, 0, 1, 0 };
    private static readonly byte[] PngTrailer = Convert.FromHexString("49454E44AE426082"); // "IEND" + CRC

    /// <summary>True when <paramref name="data"/> looks like a PSP DOCUMENT.DAT (its fixed PGD header).</summary>
    public static bool IsDocument(ReadOnlySpan<byte> data) =>
        data.Length >= 0xA0 && data[..0x10].SequenceEqual(DocHeader);

    /// <summary>
    /// Decrypts <paramref name="documentDat"/> into its manual pages (each a standalone PNG).
    /// Pass the sibling <c>DOCINFO.EDAT</c> bytes in <paramref name="docInfoEdat"/> to derive the
    /// per-document key; pass null to use the fixed default key.
    /// </summary>
    public static IReadOnlyList<byte[]> DecryptPages(byte[] documentDat, byte[]? docInfoEdat = null)
    {
        ArgumentNullException.ThrowIfNull(documentDat);
        if (!IsDocument(documentDat))
            throw new PkgFormatException("Not a PSP DOCUMENT.DAT (unexpected header).");
        if (!IsAllZero(documentDat.AsSpan(0x70, 0x10)))
            throw new PkgFormatException("DOCUMENT.DAT: expected zero padding at 0x70 is missing.");

        // Header SHA-1 HMACs (fixed keys) verify the file is an unaltered DOCUMENT.DAT.
        var signed = documentDat.AsSpan(0x10, 0x60);
        if (!Sha1Hmac(HmacKeyPsp, signed).AsSpan().SequenceEqual(documentDat.AsSpan(0x80, 0x10)) ||
            !Sha1Hmac(HmacKeyPs3, signed).AsSpan().SequenceEqual(documentDat.AsSpan(0x90, 0x10)))
            throw new PkgFormatException("DOCUMENT.DAT: header HMAC mismatch (corrupt or not a DOCUMENT.DAT).");

        byte[] key = ResolveKey(docInfoEdat);

        byte[] header = DesDecrypt(key, documentDat.AsSpan(0x10, 0x60));
        if (!header.AsSpan(0, 4).SequenceEqual(DocMagic) || !header.AsSpan(0x4, 0x8).SequenceEqual(DocVersion))
            throw new PkgKeyException("DOCUMENT.DAT: decrypted header is not 'DOC ' — wrong key (a DOCINFO.EDAT may be required).");

        uint pageLimitCode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x1c));
        long pageLimit = (long)Math.Pow(10, 2 + pageLimitCode) - 1;
        if (pageLimit is not (99 or 999))
            throw new PkgFormatException($"DOCUMENT.DAT: implausible page limit {pageLimit}.");

        int metadataSize = (pageLimit == 99 ? 0x32b8 : 0x1f4b8) - 0xA0 - 0x30;
        Require(documentDat, 0xA0, metadataSize, "metadata");
        byte[] meta = DesDecrypt(key, documentDat.AsSpan(0xA0, metadataSize));
        if (BinaryPrimitives.ReadUInt32LittleEndian(meta) != 0xFFFFFFFF)
            throw new PkgFormatException("DOCUMENT.DAT: metadata marker mismatch (wrong key or corrupt).");

        int pagesTotal = (int)BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(0x04));
        var pages = new List<byte[]>();
        for (int i = 0; i < pagesTotal; i++)
        {
            int entry = 0x08 + i * 0x80;
            if (entry + 0x10 > meta.Length) break;
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(entry));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(entry + 0x0c));
            if (offset == 0) continue;
            if (size < 0x50) throw new PkgFormatException($"DOCUMENT.DAT: page {i + 1} is too small.");
            Require(documentDat, (int)offset, (int)size, $"page {i + 1}");

            byte[] png = DecryptPage(key, documentDat.AsSpan((int)offset, (int)size));
            if (png.Length > 0) pages.Add(png);
        }
        return pages;
    }

    private static byte[] DecryptPage(byte[] key, ReadOnlySpan<byte> pageSpan)
    {
        // Trailing 0x30 bytes are the page's padding + HMACs, not part of the payload.
        int bodyLen = pageSpan.Length - 0x30;
        byte[] page = pageSpan[..bodyLen].ToArray();

        byte[] head = DesDecrypt(key, page.AsSpan(0, 0x20));
        int encChunks = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x08));
        int payloadOffset = 0x20 + encChunks * 0x08;
        if (payloadOffset < 0 || payloadOffset > page.Length)
            throw new PkgFormatException("DOCUMENT.DAT: page sub-header out of range.");

        byte[] sub = DesDecrypt(key, page.AsSpan(0x20, encChunks * 0x08));
        byte[] body = page.AsSpan(payloadOffset).ToArray();
        for (int j = 0; j < encChunks; j++)
        {
            uint co = BinaryPrimitives.ReadUInt32LittleEndian(sub.AsSpan(j * 8));
            uint cs = BinaryPrimitives.ReadUInt32LittleEndian(sub.AsSpan(j * 8 + 4));
            if ((long)co + cs > body.Length)
                throw new PkgFormatException("DOCUMENT.DAT: page chunk out of range.");
            DesDecrypt(key, body.AsSpan((int)co, (int)cs)).CopyTo(body, (int)co);
        }

        int end = LastIndexOf(body, PngTrailer);
        return end < 0 ? Array.Empty<byte>() : body.AsSpan(0, end + PngTrailer.Length).ToArray();
    }

    /// <summary>Resolves the DES key: derived from DOCINFO.EDAT when supplied, else the default key.</summary>
    private static byte[] ResolveKey(byte[]? docInfoEdat)
    {
        if (docInfoEdat is not { Length: > 0 })
            return DefaultDesKey;

        byte[] docKey;
        try
        {
            docKey = PspEdatFile.DecryptToArray(new MemoryStream(docInfoEdat));
        }
        catch (Exception ex) when (ex is PkgFormatException or PkgKeyException)
        {
            throw new PkgKeyException($"DOCUMENT.DAT: could not recover the key from DOCINFO.EDAT ({ex.Message}).");
        }
        if (docKey.Length < 8)
            throw new PkgKeyException("DOCUMENT.DAT: DOCINFO.EDAT payload is too short to hold a key.");

        var key = new byte[8];
        for (int i = 0; i < 8; i++) key[i] = (byte)(docKey[i] ^ DocKeyXor[i]);
        return key;
    }

    private static byte[] DesDecrypt(byte[] key, ReadOnlySpan<byte> data)
    {
        using var des = DES.Create();
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.None;
        des.Key = key;
        des.IV = DesIv;
        byte[] input = data.ToArray();
        using var dec = des.CreateDecryptor();
        return dec.TransformFinalBlock(input, 0, input.Length);
    }

    private static byte[] Sha1Hmac(byte[] key, ReadOnlySpan<byte> data)
    {
        using var h = new HMACSHA1(key);
        return h.ComputeHash(data.ToArray()).AsSpan(0, 0x10).ToArray();
    }

    private static void Require(byte[] data, int offset, int length, string what)
    {
        if (offset < 0 || length < 0 || (long)offset + length > data.Length)
            throw new PkgFormatException($"DOCUMENT.DAT: {what} range [0x{offset:X}, +0x{length:X}) is out of bounds.");
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) if (b != 0) return false;
        return true;
    }

    private static int LastIndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = haystack.Length - needle.Length; i >= 0; i--)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
