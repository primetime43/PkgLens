using System.Buffers.Binary;
using System.Text;

namespace PkgLens.Core.Tests.TestData;

/// <summary>
/// Builds a minimal, structurally-valid SELF header in-memory for parser tests — SCE header, SELF
/// extended header, app_info, a plaintext ELF header, and (optionally) an NPDRM control-info block.
/// No real key material or copyrighted content; only the header bytes the parser reads.
/// </summary>
public sealed class SyntheticSelfBuilder
{
    public ushort KeyRevision { get; set; } = 0x0004;
    public uint ProgramType { get; set; } = 8;        // NPDRM
    public ulong AuthId { get; set; } = 0x1010000001000003;
    public uint VendorId { get; set; } = 0x01000002;
    public ulong Version { get; set; } = 0x0001000000000000;
    public ulong DataLength { get; set; } = 0x11668;  // decrypted ELF size

    public bool Elf64 { get; set; } = true;
    public bool ElfBigEndian { get; set; } = true;
    public ushort ElfType { get; set; } = 2;          // EXEC
    public ushort ElfMachine { get; set; } = 0x15;    // PPC64

    public string? NpdrmContentId { get; set; } = "UP0001-NPUB30910_00-EXAMPLE000000001";
    public uint NpdrmLicenseType { get; set; } = 3;   // Free
    public uint NpdrmAppType { get; set; } = 0x21;

    // Fixed layout offsets.
    private const int AppInfoOffset = 0x70;
    private const int ElfOffset = 0x90;
    private const int ControlInfoOffset = 0xD0;
    private const int ControlInfoSize = 0x90;

    public byte[] Build()
    {
        int total = ControlInfoOffset + ControlInfoSize;
        var b = new byte[total];

        // ---- SCE header (0x20) ----
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x00), 0x53434500); // "SCE\0"
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x04), 2);          // version
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x08), KeyRevision);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x0A), 1);          // category: SELF
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x0C), 0x4A0);      // metadata_offset
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x10), (ulong)total);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x18), DataLength);

        // ---- SELF extended header (at 0x20) ----
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x20), 3);                    // self header type
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x28), AppInfoOffset);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x30), ElfOffset);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x58), ControlInfoOffset);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(0x60), ControlInfoSize);

        // ---- app_info (0x20) ----
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(AppInfoOffset + 0x00), AuthId);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(AppInfoOffset + 0x08), VendorId);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(AppInfoOffset + 0x0C), ProgramType);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(AppInfoOffset + 0x10), Version);

        // ---- embedded ELF header ----
        b[ElfOffset + 0] = 0x7F; b[ElfOffset + 1] = (byte)'E'; b[ElfOffset + 2] = (byte)'L'; b[ElfOffset + 3] = (byte)'F';
        b[ElfOffset + 4] = (byte)(Elf64 ? 2 : 1);
        b[ElfOffset + 5] = (byte)(ElfBigEndian ? 2 : 1);
        if (ElfBigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(ElfOffset + 0x10), ElfType);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(ElfOffset + 0x12), ElfMachine);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(ElfOffset + 0x10), ElfType);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(ElfOffset + 0x12), ElfMachine);
        }

        // ---- control info: a single NPDRM block ----
        if (NpdrmContentId is not null)
        {
            int c = ControlInfoOffset;
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(c + 0x00), 3);      // type: NPDRM
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(c + 0x04), 0x90);   // block size
            BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(c + 0x08), 0);      // next
            int p = c + 0x10;
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p + 0x00), 0x4E504400); // "NPD\0"
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p + 0x04), 1);          // version
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p + 0x08), NpdrmLicenseType);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p + 0x0C), NpdrmAppType);
            var cid = Encoding.ASCII.GetBytes(NpdrmContentId);
            Array.Copy(cid, 0, b, p + 0x10, Math.Min(cid.Length, 0x30));
        }

        return b;
    }
}
