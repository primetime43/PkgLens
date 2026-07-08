using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Formats.Ps3;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;

namespace PkgLens.Core;

/// <summary>
/// Rebuilds ("repacks") a package to a new stream, optionally replacing the data of individual
/// entries. The header CMAC and ECDSA signature are <b>not</b> recomputed — PkgLens never forges
/// signatures — so a repacked <em>retail</em> package is unsigned and will not pass integrity
/// checks or install on a real console. Debug packages repack cleanly.
///
/// Layout of the rebuilt (encrypted) data region: <c>[item table | entry names | file data]</c>,
/// with fresh offsets/sizes. Everything before <c>data_offset</c> (header + metadata) is copied
/// verbatim from the source, with only <c>total_size</c> and <c>data_size</c> patched.
/// </summary>
public static class PkgWriter
{
    /// <summary>Guard against building an unbounded region in memory. Larger packages aren't supported yet.</summary>
    public const long MaxRepackDataSize = 512L * 1024 * 1024;

    public static void Repack(
        Stream source,
        PkgInfo info,
        IReadOnlyDictionary<PkgEntry, byte[]> replacements,
        IKeyProvider keys,
        Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(destination);

        var header = info.Header;
        if (!info.IsDecrypted)
            throw new PkgFormatException("Cannot repack a package whose contents were not decrypted.");
        if (!keys.TryResolve(header, out var context, out string? reason))
            throw new PkgKeyException(reason ?? "No key available to re-encrypt the package.");

        var decryptor = context.CreateDecryptor();
        try
        {
            var entries = info.Entries;
            int itemCount = entries.Count;
            int tableLen = itemCount * PkgEntry.RecordSize;

            // Resolve each entry's plaintext content: a replacement if given, else the original
            // decrypted bytes read from the source at its current offset.
            var contents = new byte[itemCount][];
            var names = new byte[itemCount][];
            for (int i = 0; i < itemCount; i++)
            {
                var e = entries[i];
                names[i] = Encoding.UTF8.GetBytes(e.Name);

                if (replacements.TryGetValue(e, out var replacement) && !e.IsDirectory)
                    contents[i] = replacement;
                else if (e.IsDirectory || e.FileSize == 0)
                    contents[i] = Array.Empty<byte>();
                else
                    contents[i] = Ps3PackageReader.ReadEntry(source, header, decryptor, e);
            }

            long namesLen = names.Sum(n => (long)n.Length);
            long filesLen = contents.Sum(c => (long)c.Length);
            long dataSize = tableLen + namesLen + filesLen;
            if (dataSize > MaxRepackDataSize)
                throw new PkgFormatException(
                    $"Repacked data region ({dataSize:n0} bytes) exceeds the {MaxRepackDataSize:n0}-byte limit.");

            // Assemble the plaintext data region.
            var region = new byte[dataSize];

            int pos = tableLen;
            var nameOffsets = new int[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                nameOffsets[i] = pos;
                Array.Copy(names[i], 0, region, pos, names[i].Length);
                pos += names[i].Length;
            }

            var fileOffsets = new int[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                if (contents[i].Length == 0) { fileOffsets[i] = 0; continue; }
                fileOffsets[i] = pos;
                Array.Copy(contents[i], 0, region, pos, contents[i].Length);
                pos += contents[i].Length;
            }

            for (int i = 0; i < itemCount; i++)
            {
                var e = entries[i];
                var rec = region.AsSpan(i * PkgEntry.RecordSize, PkgEntry.RecordSize);
                BinaryPrimitives.WriteUInt32BigEndian(rec[0x00..], (uint)nameOffsets[i]);
                BinaryPrimitives.WriteUInt32BigEndian(rec[0x04..], (uint)names[i].Length);
                BinaryPrimitives.WriteUInt64BigEndian(rec[0x08..], (ulong)fileOffsets[i]);
                BinaryPrimitives.WriteUInt64BigEndian(rec[0x10..], (ulong)contents[i].Length);
                BinaryPrimitives.WriteUInt32BigEndian(rec[0x18..], e.RawType); // preserve type/flags
                // rec[0x1C..0x20] left zero (padding)
            }

            // Encrypt the region in place (XOR keystream — same primitive used to decrypt).
            decryptor.DecryptInPlace(region, 0);

            // Copy the header + metadata prefix verbatim, patching only the size fields.
            long dataOffset = (long)header.DataOffset;
            if (dataOffset <= 0 || dataOffset > source.Length || dataOffset > int.MaxValue)
                throw new PkgFormatException($"Invalid data_offset 0x{dataOffset:X} for repack.");

            var prefix = new byte[dataOffset];
            source.Position = 0;
            ReadExact(source, prefix);

            long totalSize = dataOffset + dataSize;
            BinaryPrimitives.WriteUInt64BigEndian(prefix.AsSpan(0x18), (ulong)totalSize);
            BinaryPrimitives.WriteUInt64BigEndian(prefix.AsSpan(0x28), (ulong)dataSize);

            destination.Write(prefix, 0, prefix.Length);
            destination.Write(region, 0, region.Length);
        }
        finally
        {
            (decryptor as IDisposable)?.Dispose();
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new PkgFormatException("Unexpected end of source while copying header/metadata.");
            read += n;
        }
    }
}
