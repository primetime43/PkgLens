using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Ps3.Self;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// Exercises the SELF → ELF decrypt pipeline (CTR metadata/section decrypt + ELF rebuild) end-to-end
/// with no keys, by round-tripping through a synthetic <b>debug</b> SELF (key revision 0x8000, which
/// skips the appldr/NPDRM keyset layers). The retail/NPDRM keyset path is verified separately,
/// byte-for-byte, against a real EBOOT.BIN → EBOOT.ELF golden pair. No copyrighted data or keys here.
/// </summary>
public class SelfDecryptorTests
{
    private const int EhdrLen = 0x40, PhdrLen = 0x38, ShdrLen = 0x40;

    [Fact]
    public void Decrypt_DebugSelf_ReproducesElfByteForByte()
    {
        byte[] elf = BuildElf(
            shdrCount: 2,
            segments: new[]
            {
                (type: 1u, data: RandomBytes(0x137)),  // PT_LOAD, non-block-aligned length
                (type: 1u, data: RandomBytes(0x40)),
                (type: 7u, data: RandomBytes(0x11)),    // another loadable-ish segment
            });

        byte[] self = EncodeDebugSelf(elf, out _);

        var result = SelfDecryptor.Decrypt(self);

        Assert.Equal(0x8001, result.KeyRevision);
        Assert.False(result.WasNpdrm);
        Assert.Equal(elf, result.Elf);
    }

    [Fact]
    public void Decrypt_FakeSignedSelf_ExtractsEmbeddedElf()
    {
        // A fSELF (key revision 0x8000 = debug/fake-signed marker) stores the ELF appended in the
        // clear; decrypting it must reproduce the input ELF byte-for-byte with no keys.
        byte[] elf = BuildElf(shdrCount: 1, segments: new[]
        {
            (type: 1u, data: RandomBytes(0x80)),
            (type: 1u, data: RandomBytes(0x33)),
        });
        byte[] fself = PkgLens.Core.Ps3.Self.SelfBuilder.MakeFakeSelf(elf, npdrm: false);

        var result = SelfDecryptor.Decrypt(fself);

        Assert.Equal(0x8000, result.KeyRevision);
        Assert.Equal(elf, result.Elf);
    }

    [Fact]
    public void Decrypt_CompressedFakeSignedSelf_RejectsCorruptZlibSegment()
    {
        byte[] elf = BuildElf(shdrCount: 1, segments: new[]
        {
            (type: 1u, data: new byte[32 * 1024]),
        });
        byte[] fself = SelfBuilder.MakeFakeSelf(elf);
        SelfInfo info = SelfReader.ParseInfo(new MemoryStream(fself));
        SelfSegment segment = Assert.Single(info.Segments);
        Assert.True(segment.Compressed);
        fself[(int)segment.Offset] ^= 0xFF;

        PkgFormatException exception = Assert.Throws<PkgFormatException>(() =>
            SelfDecryptor.Decrypt(fself));

        Assert.Contains("zlib", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_RejectsNonSelf()
    {
        var notSelf = new byte[0x80];
        notSelf[0] = 0x7F; notSelf[1] = (byte)'E'; notSelf[2] = (byte)'L'; notSelf[3] = (byte)'F';
        Assert.Throws<PkgFormatException>(() => SelfDecryptor.Decrypt(notSelf));
    }

    [Fact]
    public void Decrypt_OutOfRangeElfHeaderOffset_ThrowsFormatNotIndexException()
    {
        // A corrupt ehdr_offset (ext header @ 0x30) that points past EOF must fail as a clean
        // PkgFormatException, not an IndexOutOfRangeException from an unchecked span read.
        byte[] elf = BuildElf(shdrCount: 1, segments: new[] { (type: 1u, data: RandomBytes(0x40)) });
        byte[] self = EncodeDebugSelf(elf, out _);
        BinaryPrimitives.WriteUInt64BigEndian(self.AsSpan(0x30), 0xFFFFFFF0UL);

        Assert.Throws<PkgFormatException>(() => SelfDecryptor.Decrypt(self));
    }

    [Fact]
    public void Decrypt_OutOfRangeAppInfoOffset_ThrowsFormatNotIndexException()
    {
        byte[] elf = BuildElf(shdrCount: 1, segments: new[] { (type: 1u, data: RandomBytes(0x40)) });
        byte[] self = EncodeDebugSelf(elf, out _);
        BinaryPrimitives.WriteUInt64BigEndian(self.AsSpan(0x28), 0xFFFFFFF0UL); // prog_id_offset

        Assert.Throws<PkgFormatException>(() => SelfDecryptor.Decrypt(self));
    }

    [Fact]
    public void Decrypt_UnknownRetailKeyRevision_Throws()
    {
        byte[] elf = BuildElf(shdrCount: 0, segments: new[] { (type: 1u, data: RandomBytes(0x20)) });
        // Retail (non-debug) revision with no keyset in the table → clear error, not a crash.
        byte[] self = EncodeDebugSelf(elf, out _, keyRevision: 0x00FF, programType: 4);
        var ex = Assert.Throws<PkgFormatException>(() => SelfDecryptor.Decrypt(self));
        Assert.Contains("keyset", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ----

    /// <summary>Builds a minimal 64-bit big-endian PS3 ELF: ehdr, phdrs, contiguous segment data, shdrs.</summary>
    private static byte[] BuildElf(int shdrCount, (uint type, byte[] data)[] segments)
    {
        int phnum = segments.Length;
        int phoff = EhdrLen;
        int dataStart = phoff + phnum * PhdrLen;

        var pOffset = new int[phnum];
        int pos = dataStart;
        for (int i = 0; i < phnum; i++) { pOffset[i] = pos; pos += segments[i].data.Length; }
        int shoff = shdrCount > 0 ? pos : 0;
        int total = pos + shdrCount * ShdrLen;

        var elf = new byte[total];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 2; elf[5] = 2; elf[6] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x10), 2);        // e_type EXEC
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x12), 0x15);     // PPC64
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x20), (ulong)phoff);   // e_phoff
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x28), (ulong)shoff);   // e_shoff
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x34), EhdrLen);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x36), PhdrLen);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x38), (ushort)phnum);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x3A), ShdrLen);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x3C), (ushort)shdrCount);

        for (int i = 0; i < phnum; i++)
        {
            int p = phoff + i * PhdrLen;
            BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(p + 0x00), segments[i].type);
            BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(p + 0x08), (ulong)pOffset[i]);       // p_offset
            BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(p + 0x20), (ulong)segments[i].data.Length); // p_filesz
            segments[i].data.CopyTo(elf, pOffset[i]);
        }

        // Section headers with a recognizable, non-zero pattern (so a verbatim copy is actually tested).
        for (int i = 0; i < shdrCount; i++)
            for (int j = 0; j < ShdrLen; j++)
                elf[shoff + i * ShdrLen + j] = (byte)(0x80 + i * 0x10 + (j & 0x0F));

        return elf;
    }

    /// <summary>
    /// Wraps a plaintext ELF into a synthetic <b>debug</b> SELF: metadata info is stored plaintext
    /// (key revision 0x8000 makes the decryptor skip the keyset layers), the metadata headers are
    /// CTR-encrypted, and each program segment is a CTR-encrypted section. The inverse of
    /// <see cref="SelfDecryptor"/>'s debug path.
    /// </summary>
    // Default key revision 0x8001: the debug bit (0x8000) is set so the keyset layers are skipped, but
    // the low byte is not 0x80/0xC0, so it is not the "ELF appended in the clear" (CheckDebugSelf) form —
    // this fixture genuinely exercises the CTR metadata/section decrypt path.
    private static byte[] EncodeDebugSelf(byte[] elf, out int hSize, ushort keyRevision = 0x8001, uint programType = 4)
    {
        int phnum = BinaryPrimitives.ReadUInt16BigEndian(elf.AsSpan(0x38));
        int shnum = BinaryPrimitives.ReadUInt16BigEndian(elf.AsSpan(0x3C));
        int ephoff = (int)BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(0x20));
        int eshoff = (int)BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(0x28));

        // Header layout inside the SELF.
        const int progIdOff = 0x80, ehdrOff = 0xA0;
        int phdrOff = ehdrOff + EhdrLen;
        int shdrOff = shnum > 0 ? phdrOff + phnum * PhdrLen : 0;
        int afterShdr = (shnum > 0 ? shdrOff + shnum * ShdrLen : phdrOff + phnum * PhdrLen);
        int metaInfoPos = Align(afterShdr, 0x10);
        int seMeta = metaInfoPos - 0x20;

        // Metadata plaintext: meta_hdr + section headers + data keys.
        int keyCount = phnum * 2;
        int metaHeadersLen = 0x20 + phnum * 0x30 + keyCount * 0x10;
        int metaHeadersPos = metaInfoPos + 0x40;
        int hsize = metaHeadersPos + metaHeadersLen;
        hSize = hsize;

        // Segment data placed after the header region.
        var segFileOffset = new int[phnum];
        var pOffset = new int[phnum];
        var pFilesz = new int[phnum];
        int cur = hsize;
        for (int i = 0; i < phnum; i++)
        {
            int p = ephoff + i * PhdrLen;
            pOffset[i] = (int)BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(p + 0x08));
            pFilesz[i] = (int)BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(p + 0x20));
            segFileOffset[i] = cur;
            cur += pFilesz[i];
        }
        int total = cur;
        var b = new byte[total];

        // ---- SCE header ----
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x00), 0x53434500);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x04), 2);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x08), keyRevision);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x0A), 1);           // SELF
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x0C), (uint)seMeta);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x10), (ulong)hsize);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x18), (ulong)elf.Length);

        // ---- ext header ----
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x20), 3);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x28), progIdOff);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x30), (ulong)ehdrOff);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x38), (ulong)phdrOff);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x40), (ulong)shdrOff);
        // segment_ext / version / control-info left zero (unused by the decryptor's debug path).

        // ---- app_info ----
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(progIdOff + 0x0C), programType);

        // ---- embedded plaintext ELF header / phdrs / shdrs (copied verbatim) ----
        Array.Copy(elf, 0, b, ehdrOff, EhdrLen);
        Array.Copy(elf, ephoff, b, phdrOff, phnum * PhdrLen);
        if (shnum > 0) Array.Copy(elf, eshoff, b, shdrOff, shnum * ShdrLen);

        // ---- metadata info (plaintext: key, pad0, iv, pad0) ----
        byte[] metaKey = RandomBytes(0x10);
        byte[] metaIv = RandomBytes(0x10);
        metaKey.CopyTo(b, metaInfoPos + 0x00);
        metaIv.CopyTo(b, metaInfoPos + 0x20);

        // ---- metadata headers (plaintext, then CTR-encrypted) ----
        var mh = new byte[metaHeadersLen];
        BinaryPrimitives.WriteUInt32BigEndian(mh.AsSpan(0x0C), (uint)phnum);      // section_count
        BinaryPrimitives.WriteUInt32BigEndian(mh.AsSpan(0x10), (uint)keyCount);   // key_count

        var dataKeys = new byte[keyCount][];
        for (int k = 0; k < keyCount; k++) dataKeys[k] = RandomBytes(0x10);

        int shBase = 0x20;
        int keysBase = shBase + phnum * 0x30;
        for (int i = 0; i < phnum; i++)
        {
            var s = mh.AsSpan(shBase + i * 0x30);
            BinaryPrimitives.WriteUInt64BigEndian(s[0x00..], (ulong)segFileOffset[i]);  // data_offset
            BinaryPrimitives.WriteUInt64BigEndian(s[0x08..], (ulong)pFilesz[i]);        // data_size
            BinaryPrimitives.WriteUInt32BigEndian(s[0x10..], 2);                        // type: PHDR
            BinaryPrimitives.WriteUInt32BigEndian(s[0x14..], (uint)i);                  // program_idx
            BinaryPrimitives.WriteUInt32BigEndian(s[0x20..], 3);                        // encrypted
            BinaryPrimitives.WriteUInt32BigEndian(s[0x24..], (uint)(i * 2));            // key_idx
            BinaryPrimitives.WriteUInt32BigEndian(s[0x28..], (uint)(i * 2 + 1));        // iv_idx
            BinaryPrimitives.WriteUInt32BigEndian(s[0x2C..], 1);                        // compressed: no
        }
        for (int k = 0; k < keyCount; k++) dataKeys[k].CopyTo(mh, keysBase + k * 0x10);

        AesCtr(metaKey, metaIv, mh, 0, mh.Length);
        mh.CopyTo(b, metaHeadersPos);

        // ---- encrypted segment data ----
        for (int i = 0; i < phnum; i++)
        {
            byte[] seg = elf.AsSpan(pOffset[i], pFilesz[i]).ToArray();
            AesCtr(dataKeys[i * 2], dataKeys[i * 2 + 1], seg, 0, seg.Length);
            seg.CopyTo(b, segFileOffset[i]);
        }

        return b;
    }

    private static int Align(int v, int a) => (v % a == 0) ? v : v + (a - v % a);

    private static void AesCtr(byte[] key, byte[] iv, byte[] data, int offset, int length)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        Span<byte> counter = stackalloc byte[0x10];
        iv.CopyTo(counter);
        Span<byte> ks = stackalloc byte[0x10];
        int pos = offset, end = offset + length;
        while (pos < end)
        {
            aes.EncryptEcb(counter, ks, PaddingMode.None);
            int n = Math.Min(0x10, end - pos);
            for (int i = 0; i < n; i++) data[pos + i] ^= ks[i];
            pos += n;
            for (int i = counter.Length - 1; i >= 0; i--) if (++counter[i] != 0) break;
        }
    }

    // Deterministic-but-varied bytes; no crypto strength needed for a fixture.
    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 37 + 11) & 0xFF);
        return b;
    }
}
