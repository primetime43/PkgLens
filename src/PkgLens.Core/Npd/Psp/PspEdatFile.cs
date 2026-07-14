using System.Buffers.Binary;
using System.Text;

namespace PkgLens.Core.Npd.Psp;

/// <summary>Header fields of a PSP EDAT ("\0PSPEDAT") file.</summary>
public sealed class PspEdatInfo
{
    public int DrmType { get; init; }
    public int HeaderSize { get; init; }
    public string ContentId { get; init; } = string.Empty;
    /// <summary>True for a fixed-key EDAT this tool can decrypt (the inner PGD is fixed-key).</summary>
    public bool DrmFreeOrLocal => DrmType is 2 or 3;
}

/// <summary>
/// Decrypts a <b>PSP EDAT</b> ("\0PSPEDAT") — a different format from the PS3 NPDRM EDAT/SDAT that
/// <see cref="EdatFile"/> handles. A PSP EDAT is a small header followed by a PGD body; decryption
/// runs the PSP AMCTRL/KIRK pipeline (<see cref="PspPgd"/>). Fixed-key content (e.g. DOCINFO.EDAT and
/// DRM-free additional content) needs no user key; fuse-bound content is out of scope. Ported from
/// tpunix's <c>edata.c</c>. All fields are little-endian.
/// </summary>
public static class PspEdatFile
{
    private const int PgdHeaderPlusData = 0x90; // decrypted data begins 0x90 into the PGD

    /// <summary>The 8-byte magic: 0x00 then "PSPEDAT".</summary>
    public static bool IsPspEdat(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[0] == 0x00 && Encoding.ASCII.GetString(data.Slice(1, 7)) == "PSPEDAT";

    /// <summary>The 4-byte magic of a bare PGD ("\0PGD") — e.g. a PSP DOCUMENT.DAT, no EDAT wrapper.</summary>
    public static bool IsRawPgd(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x00 && Encoding.ASCII.GetString(data.Slice(1, 3)) == "PGD";

    /// <summary>True for any PSP-encrypted payload this decryptor handles (a PSP EDAT or a bare PGD).</summary>
    public static bool IsPspEncrypted(ReadOnlySpan<byte> data) => IsPspEdat(data) || IsRawPgd(data);

    /// <summary>True if the stream begins with the PSP EDAT magic.</summary>
    public static bool IsPspEdat(Stream source) => StartsWith(source, m => IsPspEdat(m));

    /// <summary>True if the stream begins with a PSP EDAT or bare PGD magic.</summary>
    public static bool IsPspEncrypted(Stream source) => StartsWith(source, m => IsPspEncrypted(m));

    private static bool StartsWith(Stream source, Func<byte[], bool> predicate)
    {
        if (!source.CanSeek || source.Length < 8) return false;
        long pos = source.Position;
        try
        {
            source.Position = 0;
            var m = new byte[8];
            return source.Read(m, 0, 8) == 8 && predicate(m);
        }
        finally { source.Position = pos; }
    }

    /// <summary>Parses the PSP EDAT header (no decryption).</summary>
    public static PspEdatInfo ParseHeader(ReadOnlySpan<byte> data)
    {
        if (!IsPspEdat(data))
            throw new PkgFormatException("Not a PSP EDAT ('\\0PSPEDAT').");
        if (data.Length < 0x40)
            throw new PkgFormatException("PSP EDAT header is truncated.");

        return new PspEdatInfo
        {
            DrmType = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x08)),
            HeaderSize = (int)(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x0C)) & 0x00FFFFFF),
            ContentId = Encoding.ASCII.GetString(data.Slice(0x10, 0x30)).TrimEnd('\0'),
        };
    }

    /// <summary>Decrypts the PSP EDAT to its plaintext payload.</summary>
    /// <summary>Decrypts a PSP EDAT / PGD fully into memory (used for small payloads, e.g. a DOCINFO.EDAT key).</summary>
    public static byte[] DecryptToArray(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Position = 0;
        using var ms = new MemoryStream();
        source.CopyTo(ms);
        byte[] buf = ms.ToArray();

        // A PSP EDAT wraps the PGD after a header; a bare PGD (e.g. DOCUMENT.DAT) starts with the PGD.
        int pgdOffset;
        if (IsPspEdat(buf))
            pgdOffset = ParseHeader(buf).HeaderSize; // PGD follows the EDAT header
        else if (IsRawPgd(buf))
            pgdOffset = 0;                            // bare PGD starts at offset 0
        else
            throw new PkgFormatException("Not a PSP EDAT ('\\0PSPEDAT') or bare PGD ('\\0PGD').");
        if (pgdOffset < 0 || (long)pgdOffset + PgdHeaderPlusData > buf.Length)
            throw new PkgFormatException($"PSP EDAT: PGD offset 0x{pgdOffset:X} is out of range.");

        // The PGD decryptor works (and mutates) in place; give it a fresh copy of the body each try.
        // pgd_flag selects the fixed key: 2 (dnas_key1A90) is the usual EDAT case, 1 (dnas_key1AA0) the
        // fallback for some bare PGDs. The header MAC tells us which one is right.
        byte[] body = buf[pgdOffset..];
        PkgFormatException? last = null;
        foreach (int pgdFlag in new[] { 2, 1 })
        {
            byte[] pgd = (byte[])body.Clone();
            try
            {
                int dataSize = PspPgd.Decrypt(pgd, pgd.Length, pgdFlag);
                if (PgdHeaderPlusData + dataSize > pgd.Length)
                    throw new PkgFormatException("PSP EDAT: decrypted size exceeds the file.");
                return pgd[PgdHeaderPlusData..(PgdHeaderPlusData + dataSize)];
            }
            catch (PkgFormatException ex)
            {
                last = ex;
                if (ex.Message.Contains("fuse")) break; // fuse-bound: retrying the key won't help
            }
        }
        throw last ?? new PkgFormatException("PSP EDAT: decryption failed.");
    }

    /// <summary>Decrypts the PSP EDAT, writing the plaintext payload to <paramref name="destination"/>.</summary>
    public static void Decrypt(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[] plain = DecryptToArray(source);
        destination.Write(plain, 0, plain.Length);
    }
}
