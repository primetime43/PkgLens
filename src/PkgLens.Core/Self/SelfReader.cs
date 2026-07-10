using System.Buffers.Binary;
using System.Text;

namespace PkgLens.Core.Self;

/// <summary>
/// Parses a SELF (Signed ELF) header without any keys. Reads the SCE header, the SELF extended
/// header (segment offsets), <c>app_info</c>, the embedded plaintext ELF header, and the control-info
/// blocks (including the NPDRM block with its content id). All multi-byte fields are big-endian.
/// Layout verified against real retail SELFs (EBOOT.BIN / default.self).
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

        // ---- embedded ELF header ----
        ElfIdent? elf = ParseElfIdent(buf, elfOffset, want);

        // ---- control info ----
        var npdrm = ParseControlInfo(buf, controlInfoOffset, controlInfoSize, want);

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
            Npdrm = npdrm,
        };
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

    /// <summary>Walks the control-info block chain and returns the NPDRM block if present.</summary>
    private static SelfNpdrmInfo? ParseControlInfo(byte[] buf, ulong offset, ulong size, int want)
    {
        if (offset == 0 || size == 0 || offset + size > (ulong)want)
            return null;

        int pos = (int)offset;
        int end = (int)(offset + size);

        while (pos + 0x10 <= end)
        {
            uint type = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos));
            uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos + 0x04));
            if (blockSize < 0x10 || pos + (int)blockSize > end)
                break; // malformed — stop rather than throw; header info is still useful

            if (type == 3) // NPDRM
            {
                int p = pos + 0x10;
                if (p + 0x50 <= want && BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p)) == NpdMagic)
                {
                    return new SelfNpdrmInfo
                    {
                        Version = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 0x04)),
                        RawLicenseType = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 0x08)),
                        AppType = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 0x0C)),
                        ContentId = Encoding.ASCII.GetString(buf, p + 0x10, 0x30).TrimEnd('\0'),
                    };
                }
            }

            pos += (int)blockSize;
        }

        return null;
    }

    private static ulong ReadU64(byte[] buf, int offset, string what)
    {
        if (offset + 8 > buf.Length)
            throw new PkgFormatException($"SELF header truncated while reading {what} at 0x{offset:X}.");
        return BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(offset));
    }
}
