using System.Buffers.Binary;

namespace PkgLens.Core.Tests.TestData;

/// <summary>Builds a minimal 64-bit big-endian PS3-style ELF that <c>SelfBuilder</c> / resign accept.</summary>
public static class MinimalElf
{
    private const int EhdrLen = 0x40, PhdrLen = 0x38;

    public static byte[] Build(params (uint type, byte[] data)[] segments)
    {
        if (segments.Length == 0) segments = new[] { (1u, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) };

        int phoff = EhdrLen;
        int phnum = segments.Length;
        int dataStart = phoff + phnum * PhdrLen;

        var offsets = new int[phnum];
        int pos = dataStart;
        for (int i = 0; i < phnum; i++) { offsets[i] = pos; pos += segments[i].data.Length; }
        int shoff = pos;

        var elf = new byte[pos];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 2; elf[5] = 2; elf[6] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x10), 2);       // e_type EXEC
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x12), 0x15);    // PPC64
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x20), (ulong)phoff);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x28), (ulong)shoff);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x34), EhdrLen);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x36), PhdrLen);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x38), (ushort)phnum);

        for (int i = 0; i < phnum; i++)
        {
            int p = phoff + i * PhdrLen;
            BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(p + 0x00), segments[i].type);
            BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(p + 0x08), (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(p + 0x20), (ulong)segments[i].data.Length);
            segments[i].data.CopyTo(elf, offsets[i]);
        }
        return elf;
    }
}
