using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PkgLens.Core.Crypto;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;

namespace PkgLens.Core;

/// <summary>
/// Builds a PS3 PKG from scratch out of a set of entries (the "Pack" operation). The resulting
/// package is a valid, self-contained <b>non-finalized (debug)</b> package by default: it needs no
/// key, and both PkgLens and RPCS3 can read/unpack it. A <see cref="PkgFinalization.Retail"/> build
/// is also supported when the runtime NPDRM PKG PS3 AES key is available, but — exactly as with
/// <see cref="PkgWriter"/> — the ECDSA signature is <b>not</b> forged, so a retail build is
/// <em>unsigned</em> and will not install on a real console. PkgLens never finalizes or signs.
///
/// Output layout: <c>[header 0xC0][metadata][pad→16][data region: item table | names | files]</c>.
/// The data region is written streamed (files are not buffered whole), keeping memory light for
/// large content.
/// </summary>
public sealed class PkgBuilder
{
    private sealed record Entry(string Name, PkgEntryType Kind, long Size, Func<Stream>? Open);

    /// <summary>Header + digest area size; metadata begins here, mirroring real packages.</summary>
    private const int HeaderSize = 0xC0;

    private readonly List<Entry> _entries = new();

    /// <summary>Retail (AES-CTR, unsigned) or, by default, non-finalized debug (self-contained SHA-1 keystream).</summary>
    public PkgFinalization Finalization { get; set; } = PkgFinalization.Debug;

    /// <summary>The 36-char content id, e.g. <c>UP0001-NPUB30910_00-EXAMPLE000000001</c>.</summary>
    public string ContentId { get; set; } = string.Empty;

    /// <summary>Install directory metadata (0x0A), usually the title-id folder. Optional.</summary>
    public string? InstallDirectory { get; set; }

    /// <summary>DRM type metadata (0x01). Default 3 (free / non-DRM).</summary>
    public uint DrmType { get; set; } = 3;

    /// <summary>Content type metadata (0x02). Default GameExec.</summary>
    public uint ContentType { get; set; } = (uint)PkgContentType.GameExec;

    /// <summary>Package flags metadata (0x03).</summary>
    public uint PackageFlags { get; set; }

    public IReadOnlyList<string> EntryNames => _entries.Select(e => e.Name).ToList();
    public int FileCount => _entries.Count(e => e.Kind != PkgEntryType.Folder);
    public int DirectoryCount => _entries.Count(e => e.Kind == PkgEntryType.Folder);

    /// <summary>Adds a directory entry. <paramref name="name"/> is a '/'-separated path relative to the package root.</summary>
    public PkgBuilder AddDirectory(string name)
    {
        _entries.Add(new Entry(Normalize(name), PkgEntryType.Folder, 0, null));
        return this;
    }

    /// <summary>Adds a file entry whose bytes are produced on demand by <paramref name="open"/> (streamed, not buffered).</summary>
    public PkgBuilder AddFile(string name, long size, Func<Stream> open, PkgEntryType kind = PkgEntryType.Regular)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (kind == PkgEntryType.Folder) throw new ArgumentException("Use AddDirectory for folders.", nameof(kind));
        _entries.Add(new Entry(Normalize(name), kind, size, open));
        return this;
    }

    /// <summary>Adds a file entry from in-memory bytes (convenience for small entries).</summary>
    public PkgBuilder AddFile(string name, byte[] content, PkgEntryType kind = PkgEntryType.Regular)
    {
        ArgumentNullException.ThrowIfNull(content);
        return AddFile(name, content.Length, () => new MemoryStream(content, writable: false), kind);
    }

    /// <summary>Writes the assembled package to <paramref name="destination"/>. The stream is written sequentially.</summary>
    public void Build(Stream destination, IKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(keys);
        if (_entries.Count == 0)
            throw new PkgFormatException("Nothing to pack: no entries were added.");

        byte[] contentIdBytes = Encoding.ASCII.GetBytes(ContentId ?? string.Empty);
        if (contentIdBytes.Length == 0)
            throw new PkgFormatException("A content id is required to build a package.");
        if (contentIdBytes.Length > PkgHeader.ContentIdLength)
            throw new PkgFormatException(
                $"Content id '{ContentId}' is {contentIdBytes.Length} bytes; the field holds at most {PkgHeader.ContentIdLength}.");

        // Deterministic keystream seeds derived from the content id (any consistent value round-trips
        // through the reader; deriving avoids all-zero seeds and keeps a rebuild reproducible).
        byte[] qaDigest = SHA1.HashData(contentIdBytes).AsSpan(0, 0x10).ToArray();
        byte[] dataRiv = SHA1.HashData(Encoding.ASCII.GetBytes("riv:" + ContentId)).AsSpan(0, 0x10).ToArray();

        // Resolve the cipher through the normal key plumbing (debug needs no key; retail needs one).
        var pseudoHeader = new PkgHeader
        {
            Finalization = Finalization,
            RawFinalization = Finalization == PkgFinalization.Retail ? (ushort)0x8000 : (ushort)0x0000,
            QaDigest = qaDigest,
            DataRiv = dataRiv,
        };
        if (!keys.TryResolve(pseudoHeader, out var context, out string? reason))
            throw new PkgKeyException(reason ?? "No key available to build the package.");

        int itemCount = _entries.Count;
        int tableLen = itemCount * PkgEntry.RecordSize;

        // Names come right after the item table; file data follows the names. All offsets are
        // relative to data_offset, matching the item-record layout the reader expects.
        var nameBytes = new byte[itemCount][];
        var nameOffset = new int[itemCount];
        long namePos = tableLen;
        for (int i = 0; i < itemCount; i++)
        {
            nameBytes[i] = Encoding.UTF8.GetBytes(_entries[i].Name);
            nameOffset[i] = checked((int)namePos);
            namePos += nameBytes[i].Length;
        }
        long namesLen = namePos - tableLen;

        var fileOffset = new long[itemCount];
        long filePos = tableLen + namesLen;
        for (int i = 0; i < itemCount; i++)
        {
            var e = _entries[i];
            if (e.Kind == PkgEntryType.Folder || e.Size == 0) { fileOffset[i] = 0; continue; }
            fileOffset[i] = filePos;
            filePos += e.Size;
        }
        long dataSize = filePos;

        byte[] metaBlock = BuildMetadata();
        long metadataOffset = HeaderSize;
        long dataOffset = Align(metadataOffset + metaBlock.Length, 16);
        long totalSize = dataOffset + dataSize;

        var cipher = context.CreateDecryptor();
        try
        {
            // --- Header (0xC0) ---
            var header = new byte[HeaderSize];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x00), PkgHeader.Magic);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x04),
                Finalization == PkgFinalization.Retail ? (ushort)0x8000 : (ushort)0x0000);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x06), 0x0001); // PS3
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x08), (uint)metadataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x0C), (uint)_metadataCount);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x10), (uint)metaBlock.Length);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x14), (uint)itemCount);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x18), (ulong)totalSize);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x20), (ulong)dataOffset);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x28), (ulong)dataSize);
            contentIdBytes.CopyTo(header.AsSpan(0x30));
            qaDigest.CopyTo(header.AsSpan(0x60));
            dataRiv.CopyTo(header.AsSpan(0x70));

            // Header digest area: SHA-1 (last 8 bytes) at 0xB8 over header[0x00:0x80] — no key needed.
            // For retail, the AES-CMAC at 0x80 (also over header[0x00:0x80]); this is an integrity MAC
            // with the public gpkg key, not a signature. The 0x28-byte ECDSA signature stays zero.
            Span<byte> sha = stackalloc byte[20];
            SHA1.HashData(header.AsSpan(0x00, 0x80), sha);
            sha.Slice(12, 8).CopyTo(header.AsSpan(0xB8));
            if (Finalization == PkgFinalization.Retail && context.TryGetHeaderCmacKey(out var macKey))
                AesCmac.Compute(macKey, header.AsSpan(0x00, 0x80)).CopyTo(header.AsSpan(0x80));

            destination.Write(header, 0, header.Length);

            // --- Metadata + padding up to data_offset ---
            destination.Write(metaBlock, 0, metaBlock.Length);
            long pad = dataOffset - (metadataOffset + metaBlock.Length);
            for (long i = 0; i < pad; i++) destination.WriteByte(0);

            // --- Item table (region offset 0) ---
            var table = new byte[tableLen];
            for (int i = 0; i < itemCount; i++)
            {
                var e = _entries[i];
                var rec = table.AsSpan(i * PkgEntry.RecordSize, PkgEntry.RecordSize);
                BinaryPrimitives.WriteUInt32BigEndian(rec[0x00..], (uint)nameOffset[i]);
                BinaryPrimitives.WriteUInt32BigEndian(rec[0x04..], (uint)nameBytes[i].Length);
                BinaryPrimitives.WriteUInt64BigEndian(rec[0x08..], (ulong)fileOffset[i]);
                BinaryPrimitives.WriteUInt64BigEndian(rec[0x10..], (ulong)(e.Kind == PkgEntryType.Folder ? 0 : e.Size));
                BinaryPrimitives.WriteUInt32BigEndian(rec[0x18..], (uint)e.Kind);
            }
            cipher.DecryptInPlace(table, 0); // XOR keystream — symmetric
            destination.Write(table, 0, table.Length);

            // --- Names (region offset tableLen) ---
            var names = new byte[namesLen];
            long p = 0;
            for (int i = 0; i < itemCount; i++)
            {
                nameBytes[i].CopyTo(names.AsSpan((int)p));
                p += nameBytes[i].Length;
            }
            cipher.DecryptInPlace(names, tableLen);
            destination.Write(names, 0, names.Length);

            // --- File data (streamed, encrypted at each file's region offset) ---
            for (int i = 0; i < itemCount; i++)
            {
                var e = _entries[i];
                if (e.Kind == PkgEntryType.Folder || e.Size == 0) continue;
                EncryptCopy(e, fileOffset[i], cipher, destination);
            }
        }
        finally
        {
            (cipher as IDisposable)?.Dispose();
        }
    }

    private static void EncryptCopy(Entry entry, long regionOffset, IPkgDecryptor cipher, Stream destination)
    {
        using var src = entry.Open!();
        var buffer = new byte[(int)Math.Min(1 << 20, Math.Max(entry.Size, 1))];
        long offset = regionOffset;
        long remaining = entry.Size;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = 0;
            while (read < want)
            {
                int n = src.Read(buffer, read, want - read);
                if (n <= 0)
                    throw new PkgFormatException(
                        $"Entry '{entry.Name}' produced fewer bytes than its declared size ({entry.Size}).");
                read += n;
            }
            cipher.DecryptInPlace(buffer.AsSpan(0, want), offset);
            destination.Write(buffer, 0, want);
            offset += want;
            remaining -= want;
        }
    }

    private int _metadataCount;

    private byte[] BuildMetadata()
    {
        var records = new List<(uint Id, byte[] Data)>
        {
            ((uint)PkgMetadataId.DrmType, U32(DrmType)),
            ((uint)PkgMetadataId.ContentType, U32(ContentType)),
            ((uint)PkgMetadataId.PackageFlags, U32(PackageFlags)),
        };
        if (!string.IsNullOrEmpty(InstallDirectory))
            records.Add(((uint)PkgMetadataId.InstallDirectory,
                Encoding.ASCII.GetBytes(InstallDirectory + "\0")));

        _metadataCount = records.Count;

        var block = new MemoryStream();
        Span<byte> head = stackalloc byte[8];
        foreach (var (id, data) in records)
        {
            BinaryPrimitives.WriteUInt32BigEndian(head[0..], id);
            BinaryPrimitives.WriteUInt32BigEndian(head[4..], (uint)data.Length);
            block.Write(head);
            block.Write(data, 0, data.Length);
        }
        return block.ToArray();

        static byte[] U32(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            return b;
        }
    }

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return name.Replace('\\', '/').Trim('/');
    }

    private static long Align(long value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;
}
