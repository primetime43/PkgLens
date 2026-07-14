using System;
using System.Buffers.Binary;
using System.IO;
using PkgLens.Core.Self;
using Xunit;

namespace PkgLens.Core.Tests;

public class SelfBuilderTests
{
    /// <summary>Builds a minimal 64-bit big-endian PS3-style ELF with the given program segments.</summary>
    private static byte[] BuildElf((uint type, byte[] data)[] segments)
    {
        const int ehdrLen = 0x40, phdrLen = 0x38;
        int phoff = ehdrLen;
        int phnum = segments.Length;
        int dataStart = phoff + phnum * phdrLen;

        // Lay out segment data after the program headers.
        var offsets = new int[phnum];
        int pos = dataStart;
        for (int i = 0; i < phnum; i++) { offsets[i] = pos; pos += segments[i].data.Length; }
        int shoff = pos; // pretend section headers sit at the end

        var elf = new byte[pos];

        // ELF header (big-endian, ELFCLASS64 / ELFDATA2MSB).
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 2; elf[5] = 2; elf[6] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x10), 2);       // e_type EXEC
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x12), 0x15);    // e_machine PPC64
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x14), 1);       // e_version
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x20), (ulong)phoff);  // e_phoff
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x28), (ulong)shoff);  // e_shoff
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x34), ehdrLen); // e_ehsize
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x36), phdrLen); // e_phentsize
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x38), (ushort)phnum); // e_phnum

        // Program headers + data.
        for (int i = 0; i < phnum; i++)
        {
            int p = phoff + i * phdrLen;
            BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(p + 0x00), segments[i].type);   // p_type
            BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(p + 0x08), (ulong)offsets[i]);  // p_offset
            BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(p + 0x20), (ulong)segments[i].data.Length); // p_filesz
            segments[i].data.CopyTo(elf, offsets[i]);
        }
        return elf;
    }

    [Fact]
    public void MakeFakeSelf_ProducesParseableFakeSelf()
    {
        byte[] elf = BuildElf(new (uint, byte[])[]
        {
            (1u, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),  // PT_LOAD
            (7u, new byte[] { 9, 9 }),                     // non-LOAD
        });

        byte[] fself = SelfBuilder.MakeFakeSelf(elf, npdrm: false);

        var info = SelfReader.ParseInfo(new MemoryStream(fself));
        Assert.Equal(SceCategory.Self, info.Category);
        Assert.Equal(0x8000, info.KeyRevision);
        Assert.True(info.IsLikelyFakeSigned);
        Assert.Equal(SelfProgramType.Application, info.ProgramType); // non-NPDRM → app type 4
        Assert.Equal((ulong)elf.Length, info.DataLength);
        Assert.NotNull(info.Elf);
        Assert.True(info.Elf!.IsBigEndian);
    }

    [Fact]
    public void MakeFakeSelf_EmbedsElfVerbatim_AtHeaderLength()
    {
        byte[] elf = BuildElf(new (uint, byte[])[] { (1u, new byte[] { 10, 20, 30, 40 }) });
        byte[] fself = SelfBuilder.MakeFakeSelf(elf);

        var info = SelfReader.ParseInfo(new MemoryStream(fself));
        // The whole ELF is appended at header_len; extracting it must reproduce the input byte-for-byte.
        int at = (int)info.HeaderLength;
        byte[] roundTripped = fself[at..(at + elf.Length)];
        Assert.Equal(elf, roundTripped);
    }

    [Fact]
    public void MakeFakeSelf_Npdrm_TagsProgramTypeNpdrm()
    {
        byte[] elf = BuildElf(new (uint, byte[])[] { (1u, new byte[] { 1, 2, 3, 4 }) });
        byte[] fself = SelfBuilder.MakeFakeSelf(elf, npdrm: true);

        var info = SelfReader.ParseInfo(new MemoryStream(fself));
        Assert.Equal(SelfProgramType.Npdrm, info.ProgramType);
        Assert.True(info.IsNpdrm);
    }

    [Fact]
    public void MakeFakeSelf_RejectsNonElf()
    {
        var notElf = new byte[0x80];
        notElf[0] = 0x53; notElf[1] = 0x43; notElf[2] = 0x45; // "SCE" — a SELF, not an ELF
        Assert.Throws<PkgFormatException>(() => SelfBuilder.MakeFakeSelf(notElf));
    }

    [Fact]
    public void MakeFakeSelf_CustomSign_WritesCustomAppInfoAndContentId()
    {
        byte[] elf = BuildElf(new (uint, byte[])[] { (1u, new byte[] { 1, 2, 3, 4 }) });

        byte[] fself = SelfBuilder.MakeFakeSelf(elf, new SelfBuilder.FakeSelfOptions
        {
            Npdrm = true,
            AuthId = 0x1070000052000001,
            VendorId = 0x01000007,
            AppVersion = 0x0003005500000000,
            ContentId = "UP0001-NPUB30910_00-CUSTOMEBOOT00001",
        });

        var info = SelfReader.ParseInfo(new MemoryStream(fself));
        Assert.Equal(0x1070000052000001UL, info.AuthId);
        Assert.Equal(0x01000007u, info.VendorId);
        Assert.Equal(0x0003005500000000UL, info.SdkVersion);
        Assert.Equal(SelfProgramType.Npdrm, info.ProgramType);
        Assert.Equal("UP0001-NPUB30910_00-CUSTOMEBOOT00001", info.Npdrm?.ContentId);
    }

    [Fact]
    public void MakeFakeSelf_CustomSign_WritesFirmwareVersionAndControlFlags()
    {
        byte[] elf = BuildElf(new (uint, byte[])[] { (1u, new byte[] { 1, 2, 3, 4 }) });
        byte[] flags = new byte[0x20];
        for (int i = 0; i < flags.Length; i++) flags[i] = (byte)(0xA0 + i);

        byte[] fself = SelfBuilder.MakeFakeSelf(elf, new SelfBuilder.FakeSelfOptions
        {
            FirmwareVersion = 44600, // 4.46
            ControlFlags = flags,
        });

        var info = SelfReader.ParseInfo(new MemoryStream(fself));
        Assert.Equal(44600UL, info.FirmwareVersion);
        Assert.Equal("4.46", info.FirmwareVersionText);
        Assert.Equal(flags, info.ControlFlags);
        Assert.Contains(info.ControlBlocks, b => b.RawType == 1); // type-1 control-flags block added
        Assert.Contains(info.ControlBlocks, b => b.RawType == 2); // type-2 digest block still present

        // Adding a control-flags block must not break the appended-ELF round-trip.
        int at = (int)info.HeaderLength;
        Assert.Equal(elf, fself[at..(at + elf.Length)]);
    }

    [Fact]
    public void MakeFakeSelf_FirmwareVersionOnly_FitsExistingDigestBlock()
    {
        byte[] elf = BuildElf(new (uint, byte[])[] { (1u, new byte[] { 5, 5, 5, 5 }) });

        byte[] def = SelfBuilder.MakeFakeSelf(elf, new SelfBuilder.FakeSelfOptions());
        byte[] withFw = SelfBuilder.MakeFakeSelf(elf, new SelfBuilder.FakeSelfOptions { FirmwareVersion = 40000 });

        // fw_version occupies the existing digest block's slot — no layout/size change.
        Assert.Equal(def.Length, withFw.Length);
        Assert.Equal("4.00", SelfReader.ParseInfo(new MemoryStream(withFw)).FirmwareVersionText);
        Assert.Null(SelfReader.ParseInfo(new MemoryStream(def)).FirmwareVersionText); // default: none
    }

    [Fact]
    public void MakeFakeSelf_RejectsWrongLengthControlFlags()
    {
        byte[] elf = BuildElf(new (uint, byte[])[] { (1u, new byte[] { 1, 2, 3, 4 }) });
        Assert.Throws<ArgumentException>(() =>
            SelfBuilder.MakeFakeSelf(elf, new SelfBuilder.FakeSelfOptions { ControlFlags = new byte[16] }));
    }

    [Fact]
    public void MakeFakeSelf_CustomSign_DefaultsMatchLegacyOverload()
    {
        byte[] elf = BuildElf(new (uint, byte[])[] { (1u, new byte[] { 9, 8, 7, 6 }) });

        // The bool overload and an all-defaults options object must produce identical bytes.
        byte[] viaBool = SelfBuilder.MakeFakeSelf(elf, npdrm: false);
        byte[] viaOptions = SelfBuilder.MakeFakeSelf(elf, new SelfBuilder.FakeSelfOptions());
        Assert.Equal(viaBool, viaOptions);
    }
}
