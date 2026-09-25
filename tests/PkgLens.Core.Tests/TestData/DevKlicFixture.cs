using System.Buffers.Binary;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;

namespace PkgLens.Core.Tests.TestData;

public sealed class DevKlicFixture : IDisposable
{
    public const string ContentId = "UP0001-NPUB12345_00-DEVKLICTEST00001";
    public static readonly byte[] Key = Enumerable.Range(1, 16).Select(i => (byte)(i * 13)).ToArray();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pkglens-discovery-test-" + Guid.NewGuid().ToString("N"));
    public string Executable => Path.Combine(Root, "EBOOT.BIN");
    public string Target => Path.Combine(Root, "target.edat");
    public string Database => Path.Combine(Root, "keys.json");
    public string Raps => Path.Combine(Root, "raps");
    public DevKlicFixture(bool hex = false, bool self = false, int offset = 0x113, int size = 1024, int license = 3, int version = 3)
    {
        Directory.CreateDirectory(Root);
        byte[] elf = Elf(size);
        (hex ? System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(Key).ToLowerInvariant()) : Key).CopyTo(elf, offset);
        File.WriteAllBytes(Executable, self ? SelfBuilder.MakeFakeSelf(elf, npdrm: false) : elf);
        using var output = new FileStream(Target, FileMode.Create, FileAccess.ReadWrite);
        EdatWriter.WriteVerified(new MemoryStream("Content to verify"u8.ToArray()), output,
            new EdatWriteOptions { ContentId = ContentId, FileName = "target.edat", License = license, Version = version }, Key);
    }
    public static byte[] Elf(int size)
    {
        byte[] elf = new byte[size];
        "\x7f"u8.CopyTo(elf); "ELF"u8.CopyTo(elf.AsSpan(1));
        elf[4] = 2; elf[5] = 2; elf[6] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x10), 2);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x12), 21);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x34), 0x40);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x36), 0x38);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x38), 1);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x40), 1);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x48), 0x100);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x60), (ulong)size - 0x100);
        BinaryPrimitives.WriteUInt64BigEndian(elf.AsSpan(0x68), (ulong)size - 0x100);
        return elf;
    }
    public void Dispose() => Directory.Delete(Root, true);
}
