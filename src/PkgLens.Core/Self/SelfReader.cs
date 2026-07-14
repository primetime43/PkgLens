using System.Buffers.Binary;
using System.Text;

namespace PkgLens.Core.Self;

/// <summary>
/// Parses a SELF (Signed ELF) header without any keys. Reads the SCE header, the SELF extended
/// header (segment offsets), <c>app_info</c>, the embedded plaintext ELF header, the segment
/// (section-info) table, and the control-info blocks (control flags + the NPDRM block with its
/// content id). All multi-byte fields are big-endian. Layout verified against real retail SELFs
/// (EBOOT.BIN / default.self).
/// </summary>
public static class SelfReader
{
    /// <summary>SCE magic at offset 0x00: bytes 'S' 'C' 'E' 0x00.</summary>
    public const uint SceMagic = 0x53434500;

    /// <summary>NPDRM control-info magic: 'N' 'P' 'D' 0x00.</summary>
    private const uint NpdMagic = 0x4E504400;

    /// <summary>Cap on how much header we buffer — real SELF headers are a few KB.</summary>
    private const int MaxHeaderRead = 4 * 1024 * 1024;

    public static bool IsSelf(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(head) == SceMagic;

    public static SelfInfo ParseInfo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new PkgFormatException("A seekable stream is required to read a SELF.");

        long fileLen = stream.Length;
        int want = (int)Math.Min(MaxHeaderRead, fileLen);
        var buf = new byte[want];
        stream.Position = 0;
        stream.ReadExactly(buf, 0, want);

        if (want < 0x20 || BinaryPrimitives.ReadUInt32BigEndian(buf) != SceMagic)
            throw new PkgFormatException("Not a SELF/SCE file: missing 'SCE\\0' magic at offset 0.");

        // ---- SCE header (0x20) ----
        uint headerVersion = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0x04));
        ushort keyRevision = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0x08));
        ushort category = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0x0A));
        uint metadataOffset = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0x0C));
        ulong headerLen = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(0x10));
        ulong dataLen = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(0x18));

        if (category != (ushort)SceCategory.Self)
            throw new PkgFormatException(
                $"SCE file is category 0x{category:X} (not a SELF). Only SELF is supported.");

        // ---- SELF extended header (at 0x20) ----
        ulong appInfoOffset = ReadU64(buf, 0x28, "app_info offset");
        ulong elfOffset = ReadU64(buf, 0x30, "elf offset");
        ulong sectionInfoOffset = ReadU64(buf, 0x48, "section_info offset");
        ulong controlInfoOffset = ReadU64(buf, 0x58, "control_info offset");
        ulong controlInfoSize = ReadU64(buf, 0x60, "control_info size");

        // ---- app_info ----
        ulong authId = 0, sdkVersion = 0;
        uint vendorId = 0, programType = 0;
        if (appInfoOffset + 0x20 <= (ulong)want)
        {
            int a = (int)appInfoOffset;
            authId = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(a + 0x00));
            vendorId = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(a + 0x08));
            programType = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(a + 0x0C));
            sdkVersion = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(a + 0x10));
        }

        // ---- embedded ELF header (also gives the program-header count = number of segments) ----
        ElfIdent? elf = ParseElfIdent(buf, elfOffset, want);
        int segmentCount = ReadPhnum(buf, elfOffset, want, elf);

        // ---- segment (section-info) table ----
        var segments = ParseSegments(buf, sectionInfoOffset, segmentCount, want);

        // ---- control info ----
        var control = ParseControlInfo(buf, controlInfoOffset, controlInfoSize, want);

        return new SelfInfo
        {
            FileSize = fileLen,
            HeaderVersion = headerVersion,
            KeyRevision = keyRevision,
            Category = SceCategory.Self,
            MetadataOffset = metadataOffset,
            HeaderLength = headerLen,
            DataLength = dataLen,
            AuthId = authId,
            VendorId = vendorId,
            RawProgramType = programType,
            SdkVersion = sdkVersion,
            Elf = elf,
            Npdrm = control.Npdrm,
            Segments = segments,
            ControlBlocks = control.Blocks,
            ControlFlags = control.ControlFlags,
            FirmwareVersion = control.FirmwareVersion,
        };
    }

    /// <summary>Reads the embedded ELF's program-header count (e_phnum), which equals the segment count.</summary>
    private static int ReadPhnum(byte[] buf, ulong elfOffset, int want, ElfIdent? elf)
    {
        if (elf is null || elfOffset + 0x3A > (ulong)want) return 0;
        int e = (int)elfOffset;
        return elf.IsBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 0x38))
            : BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 0x38));
    }

    /// <summary>
    /// Parses the plaintext segment (section-info) table: <paramref name="count"/> entries of 0x20
    /// bytes at <paramref name="offset"/>. Bounds-checked and capped, so a corrupt count can't run away.
    /// </summary>
    private static IReadOnlyList<SelfSegment> ParseSegments(byte[] buf, ulong offset, int count, int want)
    {
        const int EntryLen = 0x20;
        const int MaxSegments = 256;
        if (offset == 0 || count <= 0) return Array.Empty<SelfSegment>();

        int n = Math.Min(count, MaxSegments);
        var list = new List<SelfSegment>(n);
        for (int i = 0; i < n; i++)
        {
            ulong entry = offset + (ulong)(i * EntryLen);
            if (entry + EntryLen > (ulong)want) break;
            int s = (int)entry;
            list.Add(new SelfSegment
            {
                Index = i,
                Offset = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(s + 0x00)),
                Size = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(s + 0x08)),
                RawCompressed = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(s + 0x10)),
                RawEncrypted = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(s + 0x1C)),
            });
        }
        return list;
    }

    private static ElfIdent? ParseElfIdent(byte[] buf, ulong elfOffset, int want)
    {
        if (elfOffset + 0x14 > (ulong)want) return null;
        int e = (int)elfOffset;
        // e_ident: 0x7F 'E' 'L' 'F'
        if (buf[e] != 0x7F || buf[e + 1] != 0x45 || buf[e + 2] != 0x4C || buf[e + 3] != 0x46)
            return null;

        bool is64 = buf[e + 4] == 2;      // EI_CLASS: 2 = ELFCLASS64
        bool bigEndian = buf[e + 5] == 2; // EI_DATA:  2 = ELFDATA2MSB

        ushort type, machine;
        if (bigEndian)
        {
            type = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 0x10));
            machine = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 0x12));
        }
        else
        {
            type = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 0x10));
            machine = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(e + 0x12));
        }

        return new ElfIdent { Is64Bit = is64, IsBigEndian = bigEndian, Type = type, Machine = machine };
    }

    private readonly struct ControlInfo
    {
        public IReadOnlyList<SelfControlBlock> Blocks { get; init; }
        public SelfNpdrmInfo? Npdrm { get; init; }
        public byte[]? ControlFlags { get; init; }
        public ulong FirmwareVersion { get; init; }
    }

    /// <summary>
    /// Walks the control-info block chain, recording each block's type/size and decoding the ones we
    /// understand without keys: the type-1 control-flags payload and the type-3 NPDRM block.
    /// </summary>
    private static ControlInfo ParseControlInfo(byte[] buf, ulong offset, ulong size, int want)
    {
        var blocks = new List<SelfControlBlock>();
        SelfNpdrmInfo? npdrm = null;
        byte[]? controlFlags = null;
        ulong firmwareVersion = 0;

        if (offset == 0 || size == 0 || offset + size > (ulong)want)
            return new ControlInfo { Blocks = blocks };

        int pos = (int)offset;
        int end = (int)(offset + size);

        while (pos + 0x10 <= end)
        {
            uint type = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos));
            uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos + 0x04));
            if (blockSize < 0x10)
                break; // malformed — stop rather than throw; header info is still useful

            blocks.Add(new SelfControlBlock { RawType = type, Size = blockSize });

            int p = pos + 0x10;
            if (type == 1 && controlFlags is null && p + 0x20 <= want) // control flags
            {
                controlFlags = buf.AsSpan(p, 0x20).ToArray();
            }
            else if (type == 2 && firmwareVersion == 0 && blockSize >= 0x40 && p + 0x30 <= want) // digest (0x40)
            {
                // ci_data_digest_40: digest1[0x14] + digest2[0x14] + fw_version (decimal, big-endian).
                firmwareVersion = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(p + 0x28));
            }
            else if (type == 3 && npdrm is null) // NPDRM
            {
                // Read the NPD payload whenever it fits the buffer, even if the block's declared size
                // overshoots the control-info region (fSELFs from make_fself size this block as 0x90
                // though the payload is 0x80).
                if (p + 0x50 <= want && BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p)) == NpdMagic)
                {
                    npdrm = new SelfNpdrmInfo
                    {
                        Version = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 0x04)),
                        RawLicenseType = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 0x08)),
                        AppType = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 0x0C)),
                        ContentId = Encoding.ASCII.GetString(buf, p + 0x10, 0x30).TrimEnd('\0'),
                    };
                }
            }

            if (pos + (int)blockSize > end)
                break; // last block overshoots the region — nothing more to walk
            pos += (int)blockSize;
        }

        return new ControlInfo
        {
            Blocks = blocks,
            Npdrm = npdrm,
            ControlFlags = controlFlags,
            FirmwareVersion = firmwareVersion,
        };
    }

    private static ulong ReadU64(byte[] buf, int offset, string what)
    {
        if (offset + 8 > buf.Length)
            throw new PkgFormatException($"SELF header truncated while reading {what} at 0x{offset:X}.");
        return BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(offset));
    }
}
