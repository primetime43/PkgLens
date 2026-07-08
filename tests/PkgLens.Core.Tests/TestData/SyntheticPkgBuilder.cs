using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Crypto;
using PkgLens.Core.Models;

namespace PkgLens.Core.Tests.TestData;

/// <summary>
/// Builds a valid, self-contained PS3 PKG in-memory for tests — no copyrighted content, no real
/// keys. Because the PKG ciphers are XOR keystreams, the same <see cref="IPkgDecryptor"/> used to
/// read a package is used here to <em>encrypt</em> the data region.
/// </summary>
public sealed class SyntheticPkgBuilder
{
    private sealed record Item(string Name, byte[] Content, bool IsDirectory);

    private readonly List<Item> _items = new();
    private readonly List<(uint Id, byte[] Data)> _metadata = new();

    public PkgFinalization Finalization { get; set; } = PkgFinalization.Debug;
    public string ContentId { get; set; } = "UP0001-NPUB30910_00-EXAMPLE000000001";
    public byte[] QaDigest { get; set; } = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 1)).ToArray();
    public byte[] DataRiv { get; set; } = Enumerable.Range(0, 16).Select(i => (byte)(0xF0 - i)).ToArray();

    /// <summary>The 16-byte test AES key used for retail fixtures (never a real console key).</summary>
    public byte[] RetailAesKey { get; set; } =
        Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

    public SyntheticPkgBuilder AddDirectory(string name)
    {
        _items.Add(new Item(name, Array.Empty<byte>(), true));
        return this;
    }

    public SyntheticPkgBuilder AddFile(string name, byte[] content)
    {
        _items.Add(new Item(name, content, false));
        return this;
    }

    public SyntheticPkgBuilder AddFile(string name, string utf8) => AddFile(name, Encoding.UTF8.GetBytes(utf8));

    public SyntheticPkgBuilder AddMetadataU32(uint id, uint value)
    {
        var d = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(d, value);
        _metadata.Add((id, d));
        return this;
    }

    public SyntheticPkgBuilder AddMetadataString(uint id, string value)
    {
        _metadata.Add((id, Encoding.ASCII.GetBytes(value + "\0")));
        return this;
    }

    private IPkgDecryptor CreateCipher() => Finalization == PkgFinalization.Retail
        ? new RetailAesCtrDecryptor(RetailAesKey, DataRiv)
        : new DebugSha1Decryptor(QaDigest);

    public byte[] Build()
    {
        int itemCount = _items.Count;
        int tableLen = itemCount * PkgEntry.RecordSize;

        // Lay out names, then file data, both relative to the start of the data region.
        var names = new MemoryStream();
        var nameOffsets = new int[itemCount];
        var nameSizes = new int[itemCount];
        for (int i = 0; i < itemCount; i++)
        {
            var nb = Encoding.UTF8.GetBytes(_items[i].Name);
            nameOffsets[i] = tableLen + (int)names.Length;
            nameSizes[i] = nb.Length;
            names.Write(nb, 0, nb.Length);
        }
        int namesLen = (int)names.Length;

        var files = new MemoryStream();
        var fileOffsets = new int[itemCount];
        for (int i = 0; i < itemCount; i++)
        {
            if (_items[i].IsDirectory || _items[i].Content.Length == 0)
            {
                fileOffsets[i] = 0;
                continue;
            }
            fileOffsets[i] = tableLen + namesLen + (int)files.Length;
            files.Write(_items[i].Content, 0, _items[i].Content.Length);
        }
        int filesLen = (int)files.Length;

        // Assemble the plaintext data region: [item table | names | file data].
        int dataSize = tableLen + namesLen + filesLen;
        var data = new byte[dataSize];
        for (int i = 0; i < itemCount; i++)
        {
            var rec = data.AsSpan(i * PkgEntry.RecordSize, PkgEntry.RecordSize);
            BinaryPrimitives.WriteUInt32BigEndian(rec[0x00..], (uint)nameOffsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(rec[0x04..], (uint)nameSizes[i]);
            BinaryPrimitives.WriteUInt64BigEndian(rec[0x08..], (ulong)fileOffsets[i]);
            BinaryPrimitives.WriteUInt64BigEndian(rec[0x10..], (ulong)_items[i].Content.Length);
            uint type = _items[i].IsDirectory ? (uint)PkgEntryType.Folder : (uint)PkgEntryType.Regular;
            BinaryPrimitives.WriteUInt32BigEndian(rec[0x18..], type);
        }
        names.GetBuffer().AsSpan(0, namesLen).CopyTo(data.AsSpan(tableLen));
        files.GetBuffer().AsSpan(0, filesLen).CopyTo(data.AsSpan(tableLen + namesLen));

        // Encrypt the whole data region in place (region offset 0).
        var cipher = CreateCipher();
        cipher.DecryptInPlace(data, 0); // XOR keystream — symmetric
        (cipher as IDisposable)?.Dispose();

        // Metadata block (plaintext).
        var meta = new MemoryStream();
        Span<byte> hdr = stackalloc byte[8];
        foreach (var (id, d) in _metadata)
        {
            BinaryPrimitives.WriteUInt32BigEndian(hdr[0..], id);
            BinaryPrimitives.WriteUInt32BigEndian(hdr[4..], (uint)d.Length);
            meta.Write(hdr);
            meta.Write(d, 0, d.Length);
        }
        byte[] metaBlock = meta.ToArray();

        // Fixed layout: header 0xC0, then metadata, then (16-aligned) data region.
        const int headerSize = 0xC0;
        int metadataOffset = headerSize;
        int dataOffset = Align(metadataOffset + metaBlock.Length, 16);
        long totalSize = dataOffset + dataSize;

        var output = new byte[totalSize];
        var header = output.AsSpan(0, headerSize);

        BinaryPrimitives.WriteUInt32BigEndian(header[0x00..], PkgHeader.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header[0x04..], Finalization == PkgFinalization.Retail ? (ushort)0x8000 : (ushort)0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(header[0x06..], 0x0001); // PS3
        BinaryPrimitives.WriteUInt32BigEndian(header[0x08..], (uint)metadataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x0C..], (uint)_metadata.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x10..], (uint)metaBlock.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x14..], (uint)itemCount);
        BinaryPrimitives.WriteUInt64BigEndian(header[0x18..], (ulong)totalSize);
        BinaryPrimitives.WriteUInt64BigEndian(header[0x20..], (ulong)dataOffset);
        BinaryPrimitives.WriteUInt64BigEndian(header[0x28..], (ulong)dataSize);

        var cid = Encoding.ASCII.GetBytes(ContentId);
        cid.AsSpan(0, Math.Min(cid.Length, PkgHeader.ContentIdLength)).CopyTo(header[0x30..]);
        QaDigest.AsSpan(0, 16).CopyTo(header[0x60..]);
        DataRiv.AsSpan(0, 16).CopyTo(header[0x70..]);

        // Header digest area: SHA-1 (last 8 bytes) at 0xB8, and — for retail — the AES-CMAC at 0x80,
        // both over header[0x00:0x80], matching the confirmed real-package algorithm.
        var sha = System.Security.Cryptography.SHA1.HashData(header[0x00..0x80].ToArray());
        sha.AsSpan(12, 8).CopyTo(header[0xB8..]);
        if (Finalization == PkgFinalization.Retail)
            PkgLens.Core.Crypto.AesCmac.Compute(RetailAesKey, header[0x00..0x80]).CopyTo(header[0x80..]);

        metaBlock.CopyTo(output.AsSpan(metadataOffset));
        data.CopyTo(output.AsSpan(dataOffset));
        return output;
    }

    private static int Align(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;
}
