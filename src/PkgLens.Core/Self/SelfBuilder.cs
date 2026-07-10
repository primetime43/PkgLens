using System.Buffers.Binary;

namespace PkgLens.Core.Self;

/// <summary>
/// Builds a <b>fake-signed SELF</b> (fSELF) from a plaintext PS3 ELF — the "resign to NON-DRM"
/// operation. A fSELF carries key revision <c>0x8000</c>, which tells a jailbroken (CFW) console's
/// loader to skip signature/decryption and run the embedded ELF directly. No keys are used: this is
/// a pure container transform (the ELF segments are stored unencrypted), so it will only load on a
/// console whose signature checks are patched (CFW) — never on stock retail.
///
/// Layout and constants ported faithfully from PS3Py's <c>fself.py</c> (phiren), including its
/// alignment convention (a fully-aligned address still advances a whole block).
/// </summary>
public static class SelfBuilder
{
    // Fixed struct sizes, matching fself.py's Struct lengths.
    private const int SelfHeaderLen = 0x68;
    private const int AppInfoLen = 0x18;
    private const int ElfHeaderLen = 0x40;   // Elf64 ehdr
    private const int PhdrLen = 0x38;        // Elf64 phdr
    private const int SectionInfoLen = 0x20;
    private const int SubHeaderLen = 0x10;
    private const int Type2PayloadLen = 0x30;

    private const int PtLoad = 1;

    /// <summary>The "cap flags" digest constant fself.py writes (identical in real retail SELFs).</summary>
    private static readonly byte[] Type2MagicBits =
    {
        0x62, 0x7C, 0xB1, 0x80, 0x8A, 0xB9, 0x38, 0xE3, 0x2C, 0x8C,
        0x09, 0x17, 0x08, 0x72, 0x6A, 0x57, 0x9E, 0x25, 0x86, 0xE4,
    };

    /// <summary>fself.py's fake NPDRM content id: 0x2F bytes of 0x30 then a null.</summary>
    private static byte[] FakeNpdrmContentId()
    {
        var c = new byte[0x30];
        for (int i = 0; i < 0x2F; i++) c[i] = 0x30;
        return c;
    }

    /// <summary>
    /// Turns a plaintext ELF into a fake-signed SELF. <paramref name="npdrm"/> tags it NPDRM
    /// (app type 8) for use inside an NPDRM package; otherwise it's a plain app (type 4).
    /// </summary>
    public static byte[] MakeFakeSelf(byte[] elf, bool npdrm = false)
    {
        ArgumentNullException.ThrowIfNull(elf);
        if (elf.Length < ElfHeaderLen ||
            elf[0] != 0x7F || elf[1] != 0x45 || elf[2] != 0x4C || elf[3] != 0x46)
            throw new PkgFormatException(
                "Input is not an ELF. A fSELF is built from a decrypted ELF; an encrypted EBOOT.BIN/SELF must be decrypted first.");
        if (elf[4] != 2 || elf[5] != 2)
            throw new PkgFormatException("Only 64-bit big-endian PS3 ELFs are supported.");

        // ELF header fields (big-endian).
        ulong eShoff = BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(0x28));
        ulong ePhoff = BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(0x20));
        ushort ePhentsize = BinaryPrimitives.ReadUInt16BigEndian(elf.AsSpan(0x36));
        ushort ePhnum = BinaryPrimitives.ReadUInt16BigEndian(elf.AsSpan(0x38));

        if (ePhentsize != PhdrLen)
            throw new PkgFormatException($"Unexpected ELF phentsize 0x{ePhentsize:X} (need 0x{PhdrLen:X}).");
        if (ePhoff + (ulong)(ePhnum * PhdrLen) > (ulong)elf.Length)
            throw new PkgFormatException("ELF program headers extend past end of file.");

        // ---- Compute header offsets exactly as fself.py (note the align-adds-a-block quirk) ----
        long appInfoOff = Align(SelfHeaderLen, 0x10);                 // 0x70
        long elfOff = Align(appInfoOff + AppInfoLen, 0x10);           // 0x90
        long phdrOff = elfOff + ElfHeaderLen;                        // 0xD0
        long sectionInfoRaw = phdrOff + (long)PhdrLen * ePhnum;
        long sectionInfoOff = Align(sectionInfoRaw, 0x10);
        long controlInfoRaw = sectionInfoOff + (long)SectionInfoLen * ePhnum;
        long controlInfoOff = Align(controlInfoRaw, 0x10);

        long controlInfoSize = SubHeaderLen + Type2PayloadLen;       // 0x40 (type-2 block)
        if (npdrm)
            controlInfoSize += SubHeaderLen + 0x70;                  // + NPDRM block (fself.py sizing)

        long endOfHeader = controlInfoOff + controlInfoSize;
        long elfDataOff = Align(endOfHeader, 0x80);                  // where the whole ELF is appended
        long shdrOff = elfDataOff + (long)eShoff;
        long metadataOff = endOfHeader - 0x10;

        long total = elfDataOff + elf.Length;
        var outMs = new MemoryStream((int)total);

        // ---- SCE header + SELF extended header (0x68) ----
        Span<byte> h = stackalloc byte[SelfHeaderLen];
        BinaryPrimitives.WriteUInt32BigEndian(h[0x00..], 0x53434500);        // magic "SCE\0"
        BinaryPrimitives.WriteUInt32BigEndian(h[0x04..], 2);                 // header version
        BinaryPrimitives.WriteUInt16BigEndian(h[0x08..], 0x8000);           // key revision — fake marker
        BinaryPrimitives.WriteUInt16BigEndian(h[0x0A..], 1);                // header type — SELF
        BinaryPrimitives.WriteUInt32BigEndian(h[0x0C..], (uint)metadataOff); // metadata offset
        BinaryPrimitives.WriteUInt64BigEndian(h[0x10..], (ulong)elfDataOff); // header length (= data offset)
        BinaryPrimitives.WriteUInt64BigEndian(h[0x18..], (ulong)elf.Length); // data length (= elf size)
        BinaryPrimitives.WriteUInt64BigEndian(h[0x20..], 3);                // self header type
        BinaryPrimitives.WriteUInt64BigEndian(h[0x28..], (ulong)appInfoOff);
        BinaryPrimitives.WriteUInt64BigEndian(h[0x30..], (ulong)elfOff);
        BinaryPrimitives.WriteUInt64BigEndian(h[0x38..], (ulong)phdrOff);
        BinaryPrimitives.WriteUInt64BigEndian(h[0x40..], (ulong)shdrOff);
        BinaryPrimitives.WriteUInt64BigEndian(h[0x48..], (ulong)sectionInfoOff);
        BinaryPrimitives.WriteUInt64BigEndian(h[0x50..], 0);                // sce_version offset — none
        BinaryPrimitives.WriteUInt64BigEndian(h[0x58..], (ulong)controlInfoOff);
        BinaryPrimitives.WriteUInt64BigEndian(h[0x60..], (ulong)controlInfoSize);
        outMs.Write(h);
        Pad(outMs, SelfHeaderLen, 0x10);

        // ---- app_info (0x18) ----
        Span<byte> ai = stackalloc byte[AppInfoLen];
        BinaryPrimitives.WriteUInt64BigEndian(ai[0x00..], 0x1010000001000003); // auth id
        BinaryPrimitives.WriteUInt32BigEndian(ai[0x08..], 0x01000002);          // vendor id
        BinaryPrimitives.WriteUInt32BigEndian(ai[0x0C..], npdrm ? 8u : 4u);     // program type
        BinaryPrimitives.WriteUInt64BigEndian(ai[0x10..], 0x0001000000000000);  // version
        outMs.Write(ai);
        Pad(outMs, appInfoOff + AppInfoLen, 0x10);

        // ---- ELF header + program headers (copied verbatim from the input ELF) ----
        outMs.Write(elf, 0, ElfHeaderLen);
        outMs.Write(elf, (int)ePhoff, ePhnum * PhdrLen);
        Pad(outMs, sectionInfoRaw, 0x10);

        // ---- section info: one entry per program header ----
        Span<byte> si = stackalloc byte[SectionInfoLen];
        for (int i = 0; i < ePhnum; i++)
        {
            int p = (int)ePhoff + i * PhdrLen;
            uint pType = BinaryPrimitives.ReadUInt32BigEndian(elf.AsSpan(p + 0x00));
            ulong pOffset = BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(p + 0x08));
            ulong pFilesz = BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(p + 0x20));

            si.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(si[0x00..], pOffset + (ulong)elfDataOff); // into appended ELF
            BinaryPrimitives.WriteUInt64BigEndian(si[0x08..], pFilesz);
            BinaryPrimitives.WriteUInt32BigEndian(si[0x10..], 1);                            // uncompressed
            BinaryPrimitives.WriteUInt32BigEndian(si[0x1C..], pType == PtLoad ? 2u : 0u);   // not encrypted
            outMs.Write(si);
        }
        Pad(outMs, controlInfoRaw, 0x10);

        // ---- control info: the type-2 "cap flags" block (+ fake NPDRM block if requested) ----
        WriteControlInfo(outMs, npdrm);
        Pad(outMs, endOfHeader, 0x80);

        // ---- the whole ELF, unencrypted ----
        outMs.Write(elf, 0, elf.Length);

        return outMs.ToArray();
    }

    private static void WriteControlInfo(Stream outMs, bool npdrm)
    {
        // Sub-header: type 2, size 0x40, cont = 1 if an NPDRM block follows.
        Span<byte> sub = stackalloc byte[SubHeaderLen];
        BinaryPrimitives.WriteUInt32BigEndian(sub[0x00..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(sub[0x04..], 0x40);
        BinaryPrimitives.WriteUInt64BigEndian(sub[0x08..], npdrm ? 1u : 0u);
        outMs.Write(sub);

        Span<byte> type2 = stackalloc byte[Type2PayloadLen];
        Type2MagicBits.CopyTo(type2);      // 0x14 magic + 0x14 digest(zero) + 0x08 pad(zero)
        outMs.Write(type2);

        if (!npdrm) return;

        // Fake NPDRM block (values verbatim from fself.py — junk, since CFW ignores them).
        BinaryPrimitives.WriteUInt32BigEndian(sub[0x00..], 3);
        BinaryPrimitives.WriteUInt32BigEndian(sub[0x04..], 0x90);
        BinaryPrimitives.WriteUInt64BigEndian(sub[0x08..], 0);
        outMs.Write(sub);

        Span<byte> npd = stackalloc byte[0x70];
        BinaryPrimitives.WriteUInt32BigEndian(npd[0x00..], 0x4E504400); // "NPD\0"
        BinaryPrimitives.WriteUInt32BigEndian(npd[0x04..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(npd[0x08..], 2);          // drm type
        BinaryPrimitives.WriteUInt32BigEndian(npd[0x0C..], 1);
        FakeNpdrmContentId().CopyTo(npd[0x10..]);
        outMs.Write(npd);
    }

    /// <summary>fself.py alignment: always advances by <c>alignment - (addr % alignment)</c> (a full block when aligned).</summary>
    private static long Align(long addr, int alignment) => addr + (alignment - addr % alignment);

    private static void Pad(Stream s, long addr, int alignment)
    {
        int n = (int)(alignment - addr % alignment);
        for (int i = 0; i < n; i++) s.WriteByte(0);
    }
}
