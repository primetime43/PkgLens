using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PkgLens.Core.Shared.Crypto;

namespace PkgLens.Core.Ps3.Self;

/// <summary>
/// Writes encrypted PPU APP/NPDRM SELF containers. Encryption and authentication hashes are real;
/// ECDSA signature fields are deliberately zero. Requires a loader with signature checks patched.
/// </summary>
public static class EncryptedSelfBuilder
{
    public sealed class Options
    {
        public SelfBuilder.FakeSelfOptions Metadata { get; init; } = new();
        public ushort KeyRevision { get; init; } = 0x0A;
        public byte[]? Klicensee { get; init; }
        /// <summary>Final basename, including case, for the NPDRM filename authentication hash.</summary>
        public string FileName { get; init; } = "EBOOT.BIN";
    }

    private sealed record Segment(byte[] Data, uint Type, bool Compressed, bool Encrypted)
    {
        public int Offset { get; set; }
    }

    public static IReadOnlyList<ushort> SupportedRevisions(bool npdrm) =>
        Enumerable.Range(0, 0x80).Select(i => (ushort)i)
            .Where(i => SelfKeyset.Find(npdrm ? 8u : 4u, i) is not null).ToArray();

    /// <summary>Builds and decrypts the result to verify exact ELF preservation before returning it.</summary>
    public static byte[] Build(byte[] elf, Options options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(elf);
        ArgumentNullException.ThrowIfNull(options);
        var m = options.Metadata ?? throw new ArgumentException("SELF metadata is required.");
        uint programType = m.Npdrm ? 8u : 4u;
        if (m.ProgramType is not null && m.ProgramType != programType)
            throw new PkgFormatException("Encrypted output supports only APP and NPDRM profiles.");
        var root = SelfKeyset.Find(programType, options.KeyRevision)
            ?? throw new PkgFormatException($"No { (m.Npdrm ? "NPDRM" : "APP") } key for revision 0x{options.KeyRevision:X2}.");
        if (m.ControlFlags is { Length: not 32 }) throw new PkgFormatException("Control flags must be 32 bytes.");
        if (elf.Length < 0x40 || elf.Length > 128 * 1024 * 1024 ||
            U32(elf, 0) != 0x7F454C46 ||
            elf[4] != 2 || elf[5] != 2 || U16(elf, 0x12) != 21)
            throw new PkgFormatException("Encrypted SELF output requires a 64-bit big-endian PS3 PPU ELF, at most 128 MiB.");
        int phoff = Range(elf, U64(elf, 0x20), (ulong)U16(elf, 0x38) * 0x38);
        int phnum = U16(elf, 0x38), shnum = U16(elf, 0x3C);
        if (phnum == 0 || U16(elf, 0x36) != 0x38 || U16(elf, 0x34) != 0x40 ||
            (shnum > 0 && U16(elf, 0x3A) != 0x40))
            throw new PkgFormatException("Invalid ELF program or section header sizes.");
        int shsize = shnum * 0x40;
        int shoff = shnum == 0 ? 0 : Range(elf, U64(elf, 0x28), (ulong)shsize);
        var segments = new List<Segment>();
        // Only representable ELF layouts are accepted; never silently discard non-segment data.
        var reconstructed = new byte[elf.Length];
        elf.AsSpan(0, 0x40).CopyTo(reconstructed);
        elf.AsSpan(phoff, phnum * 0x38).CopyTo(reconstructed.AsSpan(phoff));
        int reconstructedLength = Math.Max(0x40, phoff + phnum * 0x38);
        long storedTotal = 0;
        for (int i = 0; i < phnum; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int p = phoff + i * 0x38;
            ulong length = U64(elf, p + 0x20);
            int offset = Range(elf, U64(elf, p + 8), length);
            storedTotal += (long)length;
            if (storedTotal > 256 * 1024 * 1024)
                throw new PkgFormatException("Combined ELF segments exceed the 256 MiB output limit.");
            byte[] data = elf.AsSpan(offset, (int)length).ToArray();
            data.CopyTo(reconstructed, offset);
            if (length > 0) reconstructedLength = Math.Max(reconstructedLength, offset + data.Length);
            bool compressed = false;
            if (m.CompressSegments && data.Length > 0)
            {
                using var buffer = new MemoryStream();
                using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
                if (buffer.Length < data.Length) { data = buffer.ToArray(); compressed = true; }
            }
            segments.Add(new Segment(data, U32(elf, p), compressed, true));
        }
        if (shsize > 0)
        {
            byte[] data = elf.AsSpan(shoff, shsize).ToArray();
            data.CopyTo(reconstructed, shoff);
            reconstructedLength = Math.Max(reconstructedLength, shoff + shsize);
            segments.Add(new Segment(data, 0, false, false));
        }
        if (reconstructedLength != elf.Length || !elf.AsSpan().SequenceEqual(reconstructed))
            throw new PkgFormatException("This ELF contains data outside its program segments and section headers. Encrypted output would lose data; the source has not been changed.");

        byte[]? klic = null;
        uint license = m.NpLicenseType ?? 3;
        if (m.Npdrm)
        {
            if (license is < 1 or > 3) throw new PkgFormatException("NPDRM license must be NETWORK, LOCAL or FREE.");
            if (string.IsNullOrWhiteSpace(m.ContentId) || m.ContentId.Length > 47 || m.ContentId.Any(c => c < 0x21 || c > 0x7E))
                throw new PkgFormatException("Encrypted NPDRM output needs an ASCII content ID of 1–47 characters.");
            if (string.IsNullOrWhiteSpace(options.FileName) || options.FileName.IndexOfAny(['/', '\\', '\0']) >= 0 || options.FileName.Any(c => c > 127))
                throw new PkgFormatException("NPDRM output needs an ASCII filename without a directory.");
            if (m.NpAppType is not null and not 0 and not 1 and not 0x20 and not 0x21)
                throw new PkgFormatException("Unsupported NPDRM application type.");
            klic = options.Klicensee ?? (license == 3 ? SelfKeyset.NpKlicFree : null);
            if (klic is not { Length: 16 }) throw new PkgFormatException("LOCAL/NETWORK NPDRM output needs a 16-byte klicensee or matching RAP.");
        }

        const int app = 0x70, ehdr = 0x90, phdr = 0xD0;
        int si = Align(phdr + phnum * 0x38, 16);
        int sv = si + phnum * 0x20, ci = sv + 0x10;
        int ciSize = 0x30 + 0x40 + (m.Npdrm ? 0x90 : 0);
        int metaInfo = ci + ciSize, meta = metaInfo + 0x40;
        int keyCount = phnum * 8 + (shsize > 0 ? 6 : 0);
        int keys = meta + 0x20 + segments.Count * 0x30;
        int optional = keys + keyCount * 16, signature = optional + 0x30;
        int headerLength = Align(signature + 0x30, 0x80), total = headerLength;
        foreach (var s in segments) { s.Offset = total; total = Align(checked(total + s.Data.Length), 16); }
        var output = new byte[total];
        void W32(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(at), v);
        void W64(int at, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(at), v);
        W32(0, 0x53434500); W32(4, 2);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), options.KeyRevision);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), 1);
        W32(12, (uint)metaInfo - 0x20); W64(16, (ulong)headerLength); W64(24, (ulong)(total - headerLength));
        W64(0x20, 3); W64(0x28, app); W64(0x30, ehdr); W64(0x38, phdr);
        W64(0x40, shsize > 0 ? (ulong)segments[^1].Offset : 0);
        W64(0x48, (ulong)si); W64(0x50, (ulong)sv); W64(0x58, (ulong)ci); W64(0x60, (ulong)ciSize);
        W64(app, m.AuthId ?? 0x1010000001000003); W32(app + 8, m.VendorId ?? 0x01000002);
        W32(app + 12, programType); W64(app + 16, m.AppVersion ?? 0x0001000000000000);
        elf.AsSpan(0, 0x40).CopyTo(output.AsSpan(ehdr));
        elf.AsSpan(phoff, phnum * 0x38).CopyTo(output.AsSpan(phdr));
        W32(sv, 1); W32(sv + 8, 16);
        W32(ci, 1); W32(ci + 4, 0x30); W64(ci + 8, 1);
        m.ControlFlags?.CopyTo(output, ci + 16);
        int digest = ci + 0x30;
        W32(digest, 2); W32(digest + 4, 0x40); W64(digest + 8, m.Npdrm ? 1u : 0u);
        Convert.FromHexString("627CB1808AB938E32C8C091708726A579E2586E4").CopyTo(output, digest + 16);
        SHA1.HashData(elf).CopyTo(output, digest + 36);
        W64(digest + 56, m.FirmwareVersion ?? 0);
        if (m.Npdrm)
        {
            int block = digest + 0x40, npd = block + 16;
            W32(block, 3); W32(block + 4, 0x90);
            W32(npd, 0x4E504400); W32(npd + 4, 1); W32(npd + 8, license);
            W32(npd + 12, m.NpAppType ?? (U16(elf, 0x10) == 0xFFA4 ? 0u : 1u));
            Encoding.ASCII.GetBytes(m.ContentId!).CopyTo(output, npd + 16);
            RandomNumberGenerator.Fill(output.AsSpan(npd + 0x40, 16));
            byte[] name = output.AsSpan(npd + 16, 48).ToArray().Concat(Encoding.ASCII.GetBytes(options.FileName)).ToArray();
            AesCmac.Compute(Convert.FromHexString("9B515FEACF75064981AA604D91A54E97"), name).CopyTo(output, npd + 0x50);
            byte[] controlKey = Convert.FromHexString("6BA52976EFDA16EF3C339FB2971E256B");
            for (int i = 0; i < 16; i++) controlKey[i] ^= klic![i];
            AesCmac.Compute(controlKey, output.AsSpan(npd, 0x60)).CopyTo(output, npd + 0x60);
        }
        RandomNumberGenerator.Fill(output.AsSpan(metaInfo, 16));
        RandomNumberGenerator.Fill(output.AsSpan(metaInfo + 32, 16));
        W64(meta, (ulong)signature); W32(meta + 8, 1); W32(meta + 12, (uint)segments.Count);
        W32(meta + 16, (uint)keyCount); W32(meta + 20, 0x30);
        RandomNumberGenerator.Fill(output.AsSpan(keys, keyCount * 16));
        int keyIndex = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var s = segments[i];
            int ms = meta + 0x20 + i * 0x30, k = keys + keyIndex * 16;
            W64(ms, (ulong)s.Offset); W64(ms + 8, (ulong)s.Data.Length);
            W32(ms + 16, s.Encrypted ? 2u : 1u); W32(ms + 20, (uint)i); W32(ms + 24, 2);
            W32(ms + 28, (uint)keyIndex); W32(ms + 32, s.Encrypted ? 3u : 1u);
            W32(ms + 36, s.Encrypted ? (uint)keyIndex + 6 : uint.MaxValue);
            W32(ms + 40, s.Encrypted ? (uint)keyIndex + 7 : uint.MaxValue);
            W32(ms + 44, s.Compressed ? 2u : 1u);
            output.AsSpan(k, 32).Clear();
            HMACSHA1.HashData(output.AsSpan(k + 32, 64), s.Data).CopyTo(output, k);
            s.Data.CopyTo(output, s.Offset);
            if (s.Encrypted)
            {
                int sectionInfo = si + i * 32;
                W64(sectionInfo, (ulong)s.Offset); W64(sectionInfo + 8, (ulong)s.Data.Length);
                W32(sectionInfo + 16, s.Compressed ? 2u : 1u);
                W32(sectionInfo + 28, s.Type is 1 or 0x700000A4 or 0x700000A8 ? 1u : 0u);
                Ctr(output.AsSpan(k + 96, 16), output.AsSpan(k + 112, 16), output.AsSpan(s.Offset, s.Data.Length));
            }
            keyIndex += s.Encrypted ? 8 : 6;
        }
        W32(optional, 1); W32(optional + 4, 0x30);
        W64(optional + 32, m.Npdrm ? 0x3Bu : 0x7Bu); W32(optional + 40, 1);
        W32(optional + 44, m.Npdrm ? 0x2000u : 0x20000u);
        Ctr(output.AsSpan(metaInfo, 16), output.AsSpan(metaInfo + 32, 16), output.AsSpan(meta, headerLength - meta));
        using var aes = Aes.Create();
        aes.Key = root.Erk;
        aes.EncryptCbc(output.AsSpan(metaInfo, 64), root.Riv, PaddingMode.None).CopyTo(output, metaInfo);
        if (m.Npdrm)
        {
            aes.Key = SelfKeyset.NpKlicKey;
            aes.Key = aes.DecryptEcb(klic!, PaddingMode.None);
            aes.EncryptCbc(output.AsSpan(metaInfo, 64), new byte[16], PaddingMode.None).CopyTo(output, metaInfo);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!SelfDecryptor.Decrypt(output, klic).Elf.AsSpan().SequenceEqual(elf))
            throw new PkgFormatException("Encrypted SELF verification failed: recovered ELF differs from the source.");
        return output;
    }

    private static int Range(byte[] elf, ulong offset, ulong size)
    {
        if (offset > (ulong)elf.Length || size > (ulong)elf.Length - offset)
            throw new PkgFormatException("ELF segment/header extends outside the input file.");
        return (int)offset;
    }
    private static int Align(int n, int a) => checked(n + a - 1) & ~(a - 1);
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
    private static ulong U64(byte[] b, int p) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p));
    private static void Ctr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, Span<byte> data)
    {
        using var aes = Aes.Create(); aes.Key = key.ToArray();
        Span<byte> counter = stackalloc byte[16]; iv.CopyTo(counter);
        Span<byte> block = stackalloc byte[16];
        for (int p = 0; p < data.Length; p += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            for (int j = 0; j < Math.Min(16, data.Length - p); j++) data[p + j] ^= block[j];
            for (int j = 15; j >= 0 && ++counter[j] == 0; j--) { }
        }
    }
}
