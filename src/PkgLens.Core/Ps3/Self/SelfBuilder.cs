using System.Buffers.Binary;
using System.IO.Compression;

namespace PkgLens.Core.Ps3.Self;

/// <summary>
/// Builds a <b>fake-signed SELF</b> (fSELF) from a plaintext PS3 ELF — the "resign to NON-DRM"
/// operation. A fSELF carries key revision <c>0x8000</c>, which tells a jailbroken (CFW) console's
/// loader to skip signature/decryption and run the embedded ELF directly. No keys are used: this is
/// a pure container transform (the ELF segments are stored unencrypted), so it will only load on a
/// console whose signature checks are patched (CFW) — never on stock retail.
///
/// Layout and constants ported from PS3Py's <c>fself.py</c> (phiren), including its
/// alignment convention (a fully-aligned address still advances a whole block).
/// </summary>
public static class SelfBuilder
{
    private sealed class SegmentPayload
    {
        public required uint Type { get; init; }
        public required ulong ElfOffset { get; init; }
        public required ulong ElfSize { get; init; }
        public required byte[] StoredData { get; init; }
        public required bool Compressed { get; init; }
        public long SelfOffset { get; set; }
    }

    // Fixed struct sizes, matching fself.py's Struct lengths.
    private const int SelfHeaderLen = 0x68;
    private const int AppInfoLen = 0x18;
    private const int ElfHeaderLen = 0x40;   // Elf64 ehdr
    private const int PhdrLen = 0x38;        // Elf64 phdr
    private const int SectionInfoLen = 0x20;
    private const int SubHeaderLen = 0x10;
    private const int Type2PayloadLen = 0x30;

    private const int PtLoad = 1;

    /// <summary>The "cap flags" digest constant fself.py writes.</summary>
    private static readonly byte[] Type2MagicBits =
    {
        0x62, 0x7C, 0xB1, 0x80, 0x8A, 0xB9, 0x38, 0xE3, 0x2C, 0x8C,
        0x09, 0x17, 0x08, 0x72, 0x6A, 0x57, 0x9E, 0x25, 0x86, 0xE4,
    };

    // fself.py's default app_info values; used when a field is unset.
    private const ulong DefaultAuthId = 0x1010000001000003;
    private const uint DefaultVendorId = 0x01000002;
    private const ulong DefaultAppVersion = 0x0001000000000000;

    /// <summary>fself.py's fake NPDRM content id: 0x2F bytes of 0x30 then a null.</summary>
    private static byte[] FakeNpdrmContentId()
    {
        var c = new byte[0x30];
        for (int i = 0; i < 0x2F; i++) c[i] = 0x30;
        return c;
    }

    /// <summary>Encodes a content id into the 0x30-byte NPDRM field (ASCII, null-padded), or the fake default.</summary>
    private static byte[] ContentIdBytes(string? contentId)
    {
        if (string.IsNullOrEmpty(contentId)) return FakeNpdrmContentId();
        var c = new byte[0x30];
        var ascii = System.Text.Encoding.ASCII.GetBytes(contentId);
        Array.Copy(ascii, c, Math.Min(ascii.Length, 0x2F)); // keep the trailing null
        return c;
    }

    /// <summary>
    /// Fields written into a fake-signed SELF's <c>app_info</c> and NPDRM block (Custom Sign). Any field
    /// left null uses the fSELF default. These are metadata only — they don't affect the (keyless)
    /// signature, but let callers match a specific SDK/firmware profile or content id.
    /// </summary>
    public sealed class FakeSelfOptions
    {
        /// <summary>Tag the SELF as NPDRM (app type 8) rather than a plain app (type 4).</summary>
        public bool Npdrm { get; set; }

        /// <summary>app_info auth id (default 0x1010000001000003).</summary>
        public ulong? AuthId { get; set; }

        /// <summary>app_info vendor id (default 0x01000002).</summary>
        public uint? VendorId { get; set; }

        /// <summary>app_info version / SDK-firmware field (default 0x0001000000000000).</summary>
        public ulong? AppVersion { get; set; }

        /// <summary>Override the app_info program type outright (else 8 for NPDRM, 4 otherwise).</summary>
        public uint? ProgramType { get; set; }

        /// <summary>Content id for the NPDRM control block (NPDRM builds only; default is the fake 0x30-byte id).</summary>
        public string? ContentId { get; set; }

        /// <summary>NPDRM license type in the control block: 1 = Network, 2 = Local, 3 = Free. Null keeps the default (2).</summary>
        public uint? NpLicenseType { get; set; }

        /// <summary>NPDRM app type in the control block: SPRX = 0, EXEC = 1, USPRX = 0x20, UEXEC = 0x21. Null keeps the default (1 = EXEC).</summary>
        public uint? NpAppType { get; set; }

        /// <summary>
        /// Firmware version to record in the type-2 digest control block (scetool <c>-6</c>), as a
        /// decimal version — <c>major*10000 + minor*100</c> (e.g. 4.46 → 44600). Null leaves it 0.
        /// </summary>
        public ulong? FirmwareVersion { get; set; }

        /// <summary>
        /// A 0x20-byte control-flags payload written as a type-1 control-info block (scetool <c>-8</c>).
        /// Null omits the block entirely (the fSELF default — matching fself.py).
        /// </summary>
        public byte[]? ControlFlags { get; set; }

        /// <summary>
        /// Compress eligible program segments with zlib when doing so makes the complete fSELF smaller.
        /// Incompressible segments remain plain, and the legacy whole-ELF layout is retained when an
        /// exact ELF reconstruction cannot be guaranteed.
        /// </summary>
        public bool CompressSegments { get; set; } = true;
    }

    /// <summary>
    /// Turns a plaintext ELF into a fake-signed SELF. <paramref name="npdrm"/> tags it NPDRM
    /// (app type 8) for use inside an NPDRM package; otherwise it's a plain app (type 4).
    /// </summary>
    public static byte[] MakeFakeSelf(byte[] elf, bool npdrm = false) =>
        MakeFakeSelf(elf, new FakeSelfOptions { Npdrm = npdrm });

    /// <summary>
    /// Turns a plaintext ELF into a fake-signed SELF with custom <c>app_info</c> / NPDRM fields
    /// (Custom Sign). See <see cref="FakeSelfOptions"/>; unset fields use the fSELF defaults.
    /// </summary>
    public static byte[] MakeFakeSelf(byte[] elf, FakeSelfOptions options)
    {
        ArgumentNullException.ThrowIfNull(elf);
        ArgumentNullException.ThrowIfNull(options);
        bool npdrm = options.Npdrm;
        if (options.ControlFlags is { } cf && cf.Length != 0x20)
            throw new ArgumentException("Control flags must be exactly 0x20 (32) bytes.", nameof(options));
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
        ushort eShentsize = BinaryPrimitives.ReadUInt16BigEndian(elf.AsSpan(0x3A));
        ushort eShnum = BinaryPrimitives.ReadUInt16BigEndian(elf.AsSpan(0x3C));

        if (ePhentsize != PhdrLen)
            throw new PkgFormatException($"Unexpected ELF phentsize 0x{ePhentsize:X} (need 0x{PhdrLen:X}).");
        if (ePhoff + (ulong)(ePhnum * PhdrLen) > (ulong)elf.Length)
            throw new PkgFormatException("ELF program headers extend past end of file.");

        long sectionHeaderLength = 0;
        if (eShnum > 0)
        {
            if (eShentsize == 0)
                throw new PkgFormatException("ELF has section headers but its section-header entry size is zero.");
            ulong length = (ulong)eShentsize * eShnum;
            if (eShoff > int.MaxValue || length > int.MaxValue || eShoff + length > (ulong)elf.Length)
                throw new PkgFormatException("ELF section headers extend past end of file.");
            sectionHeaderLength = (long)length;
        }

        List<SegmentPayload> segments = ReadSegments(elf, ePhoff, ePhnum, options.CompressSegments);

        // ---- Compute header offsets exactly as fself.py (note the align-adds-a-block quirk) ----
        long appInfoOff = Align(SelfHeaderLen, 0x10);                 // 0x70
        long elfOff = Align(appInfoOff + AppInfoLen, 0x10);           // 0x90
        long phdrOff = elfOff + ElfHeaderLen;                        // 0xD0
        long sectionInfoRaw = phdrOff + (long)PhdrLen * ePhnum;
        long sectionInfoOff = Align(sectionInfoRaw, 0x10);
        long controlInfoRaw = sectionInfoOff + (long)SectionInfoLen * ePhnum;
        long controlInfoOff = Align(controlInfoRaw, 0x10);

        long controlFlagsSize = options.ControlFlags is not null ? SubHeaderLen + 0x20 : 0; // type-1 block
        long controlInfoSize = controlFlagsSize + SubHeaderLen + Type2PayloadLen;   // (+ type-1) + type-2 block
        if (npdrm)
            controlInfoSize += SubHeaderLen + 0x70;                  // + NPDRM block (fself.py sizing)

        long endOfHeader = controlInfoOff + controlInfoSize;
        long elfDataOff = Align(endOfHeader, 0x80);
        bool compressedLayout = options.CompressSegments &&
                                segments.Any(segment => segment.Compressed) &&
                                CanReconstructExactly(elf, ePhoff, ePhnum, eShoff,
                                    sectionHeaderLength, segments);

        long shdrOff = 0;
        long total = 0;
        if (compressedLayout)
        {
            long cursor = elfDataOff;
            for (int i = 0; i < segments.Count; i++)
            {
                SegmentPayload segment = segments[i];
                segment.SelfOffset = cursor;
                cursor += segment.StoredData.LongLength;
                if (i + 1 < segments.Count || sectionHeaderLength > 0)
                    cursor = AlignUp(cursor, 0x10);
            }
            shdrOff = sectionHeaderLength > 0 ? cursor : 0;
            total = shdrOff != 0 ? shdrOff + sectionHeaderLength : cursor;
            if (total - elfDataOff >= elf.Length)
                compressedLayout = false;
        }

        if (!compressedLayout)
        {
            shdrOff = elfDataOff + (long)eShoff;
            total = elfDataOff + elf.Length;
        }
        long metadataOff = endOfHeader - 0x10;
        if (total > int.MaxValue)
            throw new PkgFormatException("The generated fake-signed SELF would exceed the supported 2 GiB size.");
        var outMs = new MemoryStream((int)total);

        // ---- SCE header + SELF extended header (0x68) ----
        Span<byte> h = stackalloc byte[SelfHeaderLen];
        BinaryPrimitives.WriteUInt32BigEndian(h[0x00..], 0x53434500);        // magic "SCE\0"
        BinaryPrimitives.WriteUInt32BigEndian(h[0x04..], 2);                 // header version
        BinaryPrimitives.WriteUInt16BigEndian(h[0x08..], 0x8000);           // key revision — fake marker
        BinaryPrimitives.WriteUInt16BigEndian(h[0x0A..], 1);                // header type — SELF
        BinaryPrimitives.WriteUInt32BigEndian(h[0x0C..], (uint)metadataOff); // metadata offset
        BinaryPrimitives.WriteUInt64BigEndian(h[0x10..], (ulong)elfDataOff); // header length (= data offset)
        BinaryPrimitives.WriteUInt64BigEndian(h[0x18..], (ulong)(total - elfDataOff));
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
        BinaryPrimitives.WriteUInt64BigEndian(ai[0x00..], options.AuthId ?? DefaultAuthId);
        BinaryPrimitives.WriteUInt32BigEndian(ai[0x08..], options.VendorId ?? DefaultVendorId);
        BinaryPrimitives.WriteUInt32BigEndian(ai[0x0C..], options.ProgramType ?? (npdrm ? 8u : 4u));
        BinaryPrimitives.WriteUInt64BigEndian(ai[0x10..], options.AppVersion ?? DefaultAppVersion);
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
            SegmentPayload segment = segments[i];

            si.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(si[0x00..], compressedLayout
                ? (ulong)segment.SelfOffset
                : segment.ElfOffset + (ulong)elfDataOff);
            BinaryPrimitives.WriteUInt64BigEndian(si[0x08..], compressedLayout
                ? (ulong)segment.StoredData.LongLength
                : segment.ElfSize);
            BinaryPrimitives.WriteUInt32BigEndian(si[0x10..],
                compressedLayout && segment.Compressed ? 2u : 1u);
            BinaryPrimitives.WriteUInt32BigEndian(si[0x1C..], segment.Type == PtLoad ? 2u : 0u);
            outMs.Write(si);
        }
        Pad(outMs, controlInfoRaw, 0x10);

        // ---- control info: optional type-1 control flags, the type-2 digest block, + NPDRM if requested ----
        WriteControlInfo(outMs, npdrm, options.ContentId, options.ControlFlags, options.FirmwareVersion,
            options.NpLicenseType, options.NpAppType);
        Pad(outMs, endOfHeader, 0x80);

        if (compressedLayout)
        {
            foreach (SegmentPayload segment in segments)
            {
                PadTo(outMs, segment.SelfOffset);
                outMs.Write(segment.StoredData);
            }
            if (sectionHeaderLength > 0)
            {
                PadTo(outMs, shdrOff);
                outMs.Write(elf, (int)eShoff, (int)sectionHeaderLength);
            }
        }
        else
        {
            outMs.Write(elf, 0, elf.Length);
        }

        return outMs.ToArray();
    }

    private static List<SegmentPayload> ReadSegments(byte[] elf, ulong ePhoff, ushort ePhnum,
        bool compress)
    {
        var segments = new List<SegmentPayload>(ePhnum);
        for (int i = 0; i < ePhnum; i++)
        {
            int p = checked((int)ePhoff + i * PhdrLen);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(elf.AsSpan(p + 0x00));
            ulong offset = BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(p + 0x08));
            ulong size = BinaryPrimitives.ReadUInt64BigEndian(elf.AsSpan(p + 0x20));
            if (offset > int.MaxValue || size > int.MaxValue || offset + size > (ulong)elf.Length)
                throw new PkgFormatException($"ELF program segment {i} extends past end of file.");

            byte[] plain = elf.AsSpan((int)offset, (int)size).ToArray();
            byte[] stored = plain;
            bool compressed = false;
            if (compress && plain.Length > 0)
            {
                using var output = new MemoryStream();
                using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                    zlib.Write(plain);
                if (output.Length < plain.Length)
                {
                    stored = output.ToArray();
                    compressed = true;
                }
            }

            segments.Add(new SegmentPayload
            {
                Type = type,
                ElfOffset = offset,
                ElfSize = size,
                StoredData = stored,
                Compressed = compressed,
            });
        }
        return segments;
    }

    private static bool CanReconstructExactly(byte[] elf, ulong ePhoff, ushort ePhnum,
        ulong eShoff, long sectionHeaderLength, IReadOnlyList<SegmentPayload> segments)
    {
        var ranges = new List<(long Start, long End)>
        {
            (0, ElfHeaderLen),
            ((long)ePhoff, (long)ePhoff + (long)ePhnum * PhdrLen),
        };
        ranges.AddRange(segments.Select(segment =>
            ((long)segment.ElfOffset, (long)(segment.ElfOffset + segment.ElfSize))));
        if (sectionHeaderLength > 0)
            ranges.Add(((long)eShoff, (long)eShoff + sectionHeaderLength));

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        long coveredUntil = 0;
        foreach ((long start, long end) in ranges)
        {
            if (start > coveredUntil && HasNonZeroBytes(elf, coveredUntil, start))
                return false;
            coveredUntil = Math.Max(coveredUntil, end);
        }
        return coveredUntil == elf.LongLength;
    }

    private static bool HasNonZeroBytes(byte[] data, long start, long end)
    {
        for (long i = start; i < end; i++)
            if (data[i] != 0)
                return true;
        return false;
    }

    private static void WriteControlInfo(Stream outMs, bool npdrm, string? contentId,
        byte[]? controlFlags, ulong? firmwareVersion, uint? npLicenseType, uint? npAppType)
    {
        Span<byte> sub = stackalloc byte[SubHeaderLen];

        // Optional type-1 control-flags block (0x10 header + 0x20 flags). Its "next" is always 1
        // because the type-2 digest block always follows.
        if (controlFlags is not null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(sub[0x00..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(sub[0x04..], 0x30);
            BinaryPrimitives.WriteUInt64BigEndian(sub[0x08..], 1);
            outMs.Write(sub);
            outMs.Write(controlFlags, 0, 0x20);
        }

        // Type-2 digest block: type 2, size 0x40, next = 1 if an NPDRM block follows. The 0x30 payload
        // is the ci_data_digest_40 struct: digest1 (0x14 magic) + digest2 (0x14, left 0) + fw_version.
        BinaryPrimitives.WriteUInt32BigEndian(sub[0x00..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(sub[0x04..], 0x40);
        BinaryPrimitives.WriteUInt64BigEndian(sub[0x08..], npdrm ? 1u : 0u);
        outMs.Write(sub);

        Span<byte> type2 = stackalloc byte[Type2PayloadLen];
        Type2MagicBits.CopyTo(type2);      // digest1: 0x14 magic + digest2: 0x14 (zero)
        if (firmwareVersion is { } fw)
            BinaryPrimitives.WriteUInt64BigEndian(type2[0x28..], fw);   // fw_version at struct offset 0x28
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
        BinaryPrimitives.WriteUInt32BigEndian(npd[0x08..], npLicenseType ?? 2); // license type (default Local)
        BinaryPrimitives.WriteUInt32BigEndian(npd[0x0C..], npAppType ?? 1);     // app type (default EXEC)
        ContentIdBytes(contentId).CopyTo(npd[0x10..]);
        outMs.Write(npd);
    }

    /// <summary>Maps an NPDRM license-type name to its control-block value: NETWORK=1, LOCAL=2, FREE=3.</summary>
    public static bool TryParseNpLicenseType(string name, out uint value)
    {
        value = name?.Trim().ToUpperInvariant() switch
        {
            "NETWORK" => 1u,
            "LOCAL" => 2u,
            "FREE" => 3u,
            _ => 0u,
        };
        return value != 0;
    }

    /// <summary>Maps an NPDRM app-type name to its control-block value: SPRX=0, EXEC=1, USPRX=0x20, UEXEC=0x21.</summary>
    public static bool TryParseNpAppType(string name, out uint value)
    {
        switch (name?.Trim().ToUpperInvariant())
        {
            case "SPRX": value = 0x00; return true;
            case "EXEC": value = 0x01; return true;
            case "USPRX": value = 0x20; return true;
            case "UEXEC": value = 0x21; return true;
            default: value = 0; return false;
        }
    }

    /// <summary>fself.py alignment: always advances by <c>alignment - (addr % alignment)</c> (a full block when aligned).</summary>
    private static long Align(long addr, int alignment) => addr + (alignment - addr % alignment);

    private static long AlignUp(long value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;

    private static void PadTo(Stream stream, long position)
    {
        if (stream.Position > position)
            throw new InvalidOperationException("The generated SELF layout overlaps a previous region.");
        while (stream.Position < position)
            stream.WriteByte(0);
    }

    private static void Pad(Stream s, long addr, int alignment)
    {
        int n = (int)(alignment - addr % alignment);
        for (int i = 0; i < n; i++) s.WriteByte(0);
    }
}
