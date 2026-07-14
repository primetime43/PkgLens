using System.Buffers.Binary;

namespace PkgLens.Core.Psp;

/// <summary>One file inside a PBP container: its standard name, byte offset, and size.</summary>
public sealed record PbpEntry(string Name, long Offset, long Size);

/// <summary>
/// Parses a PSP PBP container (e.g. <c>EBOOT.PBP</c>). The header is a fixed table of eight
/// little-endian section offsets — PARAM.SFO, ICON0.PNG, ICON1.PMF, PIC0.PNG, PIC1.PNG, SND0.AT3,
/// DATA.PSP, DATA.PSAR — each section running from its offset to the next. The container itself is
/// plaintext (no keys); the inner <c>DATA.PSP</c> executable has its own encryption, handled elsewhere.
/// PBP fields are little-endian (PSP), unlike the big-endian PS3 PKG.
/// </summary>
public sealed class PbpArchive
{
    /// <summary>Bytes <c>00 'P' 'B' 'P'</c> at offset 0, read little-endian.</summary>
    public const uint Magic = 0x50425000;

    /// <summary>Header size: magic + version + eight u32 section offsets.</summary>
    public const int HeaderSize = 0x28;

    private static readonly string[] SectionNames =
    {
        "PARAM.SFO", "ICON0.PNG", "ICON1.PMF", "PIC0.PNG", "PIC1.PNG", "SND0.AT3", "DATA.PSP", "DATA.PSAR",
    };

    public uint Version { get; }

    /// <summary>The non-empty sections, in container order.</summary>
    public IReadOnlyList<PbpEntry> Entries { get; }

    private PbpArchive(uint version, IReadOnlyList<PbpEntry> entries)
    {
        Version = version;
        Entries = entries;
    }

    /// <summary>True when <paramref name="data"/> starts with the PBP magic.</summary>
    public static bool IsPbp(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    /// <summary>Parses the PBP header from a seekable stream. Reads only the header, not section data.</summary>
    public static PbpArchive Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long fileLen = stream.Length;
        if (fileLen < HeaderSize)
            throw new PkgFormatException($"PBP truncated: need at least 0x{HeaderSize:X} bytes, got 0x{fileLen:X}.");

        Span<byte> head = stackalloc byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(head);

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic)
            throw new PkgFormatException("Not a PBP: magic does not match 0x50425000 (\\0PBP).");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(head[0x04..]);

        // Eight section offsets at 0x08..0x28; each section runs to the next offset (last to EOF).
        var offsets = new long[8];
        for (int i = 0; i < 8; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(head[(0x08 + i * 4)..]);

        var entries = new List<PbpEntry>();
        for (int i = 0; i < 8; i++)
        {
            long start = offsets[i];
            long end = i < 7 ? offsets[i + 1] : fileLen;
            if (start < HeaderSize || start > fileLen || end < start || end > fileLen)
                throw new PkgFormatException(
                    $"PBP section '{SectionNames[i]}' has an out-of-range span [0x{start:X}, 0x{end:X}) in a 0x{fileLen:X}-byte file.");
            long size = end - start;
            if (size > 0)
                entries.Add(new PbpEntry(SectionNames[i], start, size));
        }

        return new PbpArchive(version, entries);
    }

    /// <summary>Reads one entry fully into memory. Intended for small sections (SFO, icons).</summary>
    public static byte[] Read(Stream stream, PbpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Size > int.MaxValue)
            throw new PkgFormatException($"PBP section '{entry.Name}' is too large to read into memory ({entry.Size:n0} bytes); stream it instead.");

        var buf = new byte[entry.Size];
        stream.Position = entry.Offset;
        stream.ReadExactly(buf, 0, buf.Length);
        return buf;
    }

    /// <summary>Streams one entry's bytes to <paramref name="destination"/> (for large sections like DATA.PSAR).</summary>
    public static void Extract(Stream source, PbpEntry entry, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

        source.Position = entry.Offset;
        long remaining = entry.Size;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, want);
            if (read <= 0)
                throw new PkgFormatException($"PBP section '{entry.Name}' is truncated (expected {entry.Size:n0} bytes).");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
