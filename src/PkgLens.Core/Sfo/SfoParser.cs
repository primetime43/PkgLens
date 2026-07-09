using System.Buffers.Binary;
using System.Text;

namespace PkgLens.Core.Sfo;

/// <summary>
/// Parses a PARAM.SFO blob. Unlike the PKG header, SFO integers are <b>little-endian</b>.
/// Layout:
/// <code>
/// Header (0x14 bytes):
///   0x00 u32 magic = 0x46535000 ("\0PSF" as little-endian)
///   0x04 u32 version
///   0x08 u32 key_table_start   (offset of the key string table)
///   0x0C u32 data_table_start  (offset of the value data table)
///   0x10 u32 entries_count
/// Index entries (0x10 bytes each, entries_count of them):
///   0x00 u16 key_offset   (relative to key_table_start)
///   0x02 u16 data_fmt
///   0x04 u32 data_len     (used bytes)
///   0x08 u32 data_max_len (allocated bytes)
///   0x0C u32 data_offset  (relative to data_table_start)
/// </code>
/// </summary>
public static class SfoParser
{
    /// <summary>Magic at offset 0x00, read as a little-endian u32 (bytes 00 'P' 'S' 'F').</summary>
    public const uint Magic = 0x46535000;

    private const int HeaderSize = 0x14;
    private const int IndexEntrySize = 0x10;

    public static SfoTable Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new PkgFormatException(
                $"PARAM.SFO truncated: need at least 0x{HeaderSize:X} bytes, got 0x{data.Length:X}.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != Magic)
            throw new PkgFormatException($"Not a PARAM.SFO: magic 0x{magic:X8} != 0x{Magic:X8}.");

        uint keyTableStart = BinaryPrimitives.ReadUInt32LittleEndian(data[0x08..]);
        uint dataTableStart = BinaryPrimitives.ReadUInt32LittleEndian(data[0x0C..]);
        uint entriesCount = BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]);

        if (keyTableStart > data.Length || dataTableStart > data.Length)
            throw new PkgFormatException("PARAM.SFO table pointers are out of range.");

        var entries = new List<SfoEntry>((int)Math.Min(entriesCount, 1024));

        for (uint i = 0; i < entriesCount; i++)
        {
            int idx = HeaderSize + (int)(i * IndexEntrySize);
            if (idx + IndexEntrySize > data.Length)
                throw new PkgFormatException($"PARAM.SFO index entry {i} is out of range.");

            var e = data[idx..];
            ushort keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(e[0x00..]);
            ushort dataFmt = BinaryPrimitives.ReadUInt16LittleEndian(e[0x02..]);
            uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(e[0x04..]);
            uint dataMaxLen = BinaryPrimitives.ReadUInt32LittleEndian(e[0x08..]);
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(e[0x0C..]);

            string key = ReadNullTerminated(data, (int)keyTableStart + keyOffset);

            long valueStart = dataTableStart + dataOffset;
            if (valueStart + dataLen > data.Length)
                throw new PkgFormatException($"PARAM.SFO value for '{key}' is out of range.");

            var valueBytes = data.Slice((int)valueStart, (int)dataLen);
            var format = (SfoFormat)dataFmt;

            string value;
            uint? intValue = null;
            if (format == SfoFormat.Int32)
            {
                intValue = valueBytes.Length >= 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(valueBytes)
                    : 0u;
                value = intValue.Value.ToString();
            }
            else
            {
                value = Encoding.UTF8.GetString(valueBytes).TrimEnd('\0');
            }

            entries.Add(new SfoEntry { Key = key, Format = format, Value = value, IntValue = intValue, MaxLength = dataMaxLen });
        }

        return new SfoTable(entries);
    }

    private static string ReadNullTerminated(ReadOnlySpan<byte> data, int start)
    {
        if (start < 0 || start >= data.Length)
            throw new PkgFormatException($"PARAM.SFO key offset 0x{start:X} is out of range.");
        int end = start;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.UTF8.GetString(data[start..end]);
    }
}
