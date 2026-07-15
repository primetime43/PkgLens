using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Tests.TestData;

/// <summary>
/// Builds a valid, self-contained PS3 PKG in-memory for tests — no copyrighted content, no real
/// keys. Because the PKG ciphers are XOR keystreams, the same <see cref="IPkgDecryptor"/> used to
/// read a package is used here to <em>encrypt</em> the data region.
/// </summary>
public sealed class SyntheticPkgBuilder
{
    private sealed record Item(string Name, byte[] Content, bool IsDirectory, byte PspTypeHigh);

    private readonly List<Item> _items = new();
    private readonly List<(uint Id, byte[] Data)> _metadata = new();

    public PkgFinalization Finalization { get; set; } = PkgFinalization.Debug;
    public string ContentId { get; set; } = "UP0001-NPUB30910_00-EXAMPLE000000001";
    public byte[] QaDigest { get; set; } = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 1)).ToArray();
    public byte[] DataRiv { get; set; } = Enumerable.Range(0, 16).Select(i => (byte)(0xF0 - i)).ToArray();

    /// <summary>The 16-byte test AES key used for retail fixtures (never a real console key).</summary>
    public byte[] RetailAesKey { get; set; } =
        Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

    /// <summary>Build a PSP/PSVita package (platform 0x0002) instead of PS3.</summary>
    public bool Psp { get; set; }

    /// <summary>The PSP key_type written at header[0xE7] (1 = PSP). Only used when <see cref="Psp"/> is set.</summary>
    public byte PspKeyType { get; set; } = 1;

    public SyntheticPkgBuilder AddDirectory(string name, byte pspTypeHigh = 0x90)
    {
        _items.Add(new Item(name, Array.Empty<byte>(), true, pspTypeHigh));
        return this;
    }

    public SyntheticPkgBuilder AddFile(string name, byte[] content, byte pspTypeHigh = 0x90)
    {
        _items.Add(new Item(name, content, false, pspTypeHigh));
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
            uint kind = _items[i].IsDirectory ? (uint)PkgEntryType.Folder : (uint)PkgEntryType.Regular;
            // PSP encodes the per-entry key selector in the type high byte; PS3 leaves it zero.
            uint type = Psp ? ((uint)_items[i].PspTypeHigh << 24) | kind : kind;
            BinaryPrimitives.WriteUInt32BigEndian(rec[0x18..], type);
        }
        names.GetBuffer().AsSpan(0, namesLen).CopyTo(data.AsSpan(tableLen));
        files.GetBuffer().AsSpan(0, filesLen).CopyTo(data.AsSpan(tableLen + namesLen));

        // Encrypt the data region in place (XOR keystream — symmetric).
        if (Psp && PspKeyType == 1)
            EncryptPspRegion(data, tableLen, nameOffsets, nameSizes, fileOffsets);
        else if (Psp)
            EncryptVitaRegion(data);
        else
        {
            var cipher = CreateCipher();
            cipher.DecryptInPlace(data, 0);
            (cipher as IDisposable)?.Dispose();
        }

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

        // Layout: header (0x100 for PSP so key_type at 0xE7 is inside it, else 0xC0), then metadata,
        // then the (16-aligned) data region.
        int headerSize = Psp ? 0x100 : 0xC0;
        int metadataOffset = headerSize;
        int dataOffset = Align(metadataOffset + metaBlock.Length, 16);
        long totalSize = dataOffset + dataSize;

        var output = new byte[totalSize];
        var header = output.AsSpan(0, headerSize);

        BinaryPrimitives.WriteUInt32BigEndian(header[0x00..], PkgHeader.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header[0x04..], Finalization == PkgFinalization.Retail ? (ushort)0x8000 : (ushort)0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(header[0x06..], Psp ? (ushort)0x0002 : (ushort)0x0001);
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
        if (Psp) header[0xE7] = PspKeyType; // key_type selector (header[0xE7] & 7)

        // Header digest area: SHA-1 (last 8 bytes) at 0xB8, and — for PS3 retail — the AES-CMAC at 0x80,
        // both over header[0x00:0x80], matching the confirmed real-package algorithm. (PSP uses a
        // different header-auth scheme, so no gpkg CMAC is written for PSP fixtures.)
        var sha = System.Security.Cryptography.SHA1.HashData(header[0x00..0x80].ToArray());
        sha.AsSpan(12, 8).CopyTo(header[0xB8..]);
        if (Finalization == PkgFinalization.Retail && !Psp)
            PkgLens.Core.Shared.Crypto.AesCmac.Compute(RetailAesKey, header[0x00..0x80]).CopyTo(header[0x80..]);

        metaBlock.CopyTo(output.AsSpan(metadataOffset));
        data.CopyTo(output.AsSpan(dataOffset));
        return output;
    }

    /// <summary>
    /// Encrypts a PSP data region: the item table with the PSP key, then each entry's name and data
    /// with its per-entry key (PSP key for type-0x90 entries, the PS3 gpkg key otherwise) — the inverse
    /// of the reader's per-entry selection.
    /// </summary>
    private void EncryptPspRegion(byte[] data, int tableLen, int[] nameOffsets, int[] nameSizes, int[] fileOffsets)
    {
        using var psp = new RetailAesCtrDecryptor(PkgLens.Core.Shared.Keys.BundledKeys.PspPkgAesKey, DataRiv);
        using var gpkg = new RetailAesCtrDecryptor(PkgLens.Core.Shared.Keys.BundledKeys.Ps3GpkgAesKey, DataRiv);

        psp.DecryptInPlace(data.AsSpan(0, tableLen), 0); // item table always uses the PSP key

        for (int i = 0; i < _items.Count; i++)
        {
            var cipher = _items[i].PspTypeHigh == 0x90 ? psp : gpkg;
            if (nameSizes[i] > 0)
                cipher.DecryptInPlace(data.AsSpan(nameOffsets[i], nameSizes[i]), nameOffsets[i]);
            int contentLen = _items[i].Content.Length;
            if (contentLen > 0)
                cipher.DecryptInPlace(data.AsSpan(fileOffsets[i], contentLen), fileOffsets[i]);
        }
    }

    private void EncryptVitaRegion(byte[] data)
    {
        byte[] key = PspKeyType switch
        {
            2 => PkgLens.Core.Shared.Keys.BundledKeys.VitaPkgAesKey2,
            3 => PkgLens.Core.Shared.Keys.BundledKeys.VitaPkgAesKey3,
            4 => PkgLens.Core.Shared.Keys.BundledKeys.VitaPkgAesKey4,
            _ => throw new InvalidOperationException($"Unsupported synthetic Vita key type {PspKeyType}."),
        };
        var header = new PkgHeader { DataRiv = DataRiv, QaDigest = QaDigest };
        var cipher = PkgLens.Core.Shared.Keys.DecryptionContext.ForVita(header, key).CreateDecryptor();
        cipher.DecryptInPlace(data, 0);
        (cipher as IDisposable)?.Dispose();
    }

    private static int Align(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;
}
