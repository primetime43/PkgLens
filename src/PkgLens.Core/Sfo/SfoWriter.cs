using System.Buffers.Binary;
using System.Text;

namespace PkgLens.Core.Sfo;

/// <summary>
/// Serializes an (edited) SFO table back to a PARAM.SFO blob, matching the little-endian layout
/// <see cref="SfoParser"/> reads. Each value keeps its allocated field size (<c>data_max_len</c>)
/// when the edited value still fits, and grows the field only if it must.
/// </summary>
public static class SfoWriter
{
    private const int HeaderSize = 0x14;
    private const int IndexEntrySize = 0x10;
    private const uint Version = 0x00000101; // 1.1

    /// <summary>Defensive ceiling on any single value's allocated size (real fields are tiny).</summary>
    private const int MaxFieldLength = 0x10000; // 64 KiB

    public static byte[] Write(IReadOnlyList<SfoEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // Per-entry value bytes + allocated size.
        var values = new byte[entries.Count][];
        var maxLens = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            values[i] = EncodeValue(entries[i]);
            if (values[i].Length > MaxFieldLength)
                throw new PkgFormatException(
                    $"PARAM.SFO value for '{entries[i].Key}' is 0x{values[i].Length:X} bytes, exceeding the 0x{MaxFieldLength:X} limit.");
            // Honor the entry's allocated size, but never below the value and never above the cap —
            // a hostile data_max_len must not drive an unbounded pad below.
            int requested = Math.Min((int)Math.Min(entries[i].MaxLength, (uint)MaxFieldLength), MaxFieldLength);
            maxLens[i] = Math.Max(requested, values[i].Length);
        }

        // Key table.
        var keyTable = new MemoryStream();
        var keyOffsets = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            keyOffsets[i] = (int)keyTable.Length;
            var kb = Encoding.UTF8.GetBytes(entries[i].Key);
            keyTable.Write(kb, 0, kb.Length);
            keyTable.WriteByte(0);
        }
        while (keyTable.Length % 4 != 0) keyTable.WriteByte(0);

        int keyTableStart = HeaderSize + entries.Count * IndexEntrySize;
        int dataTableStart = keyTableStart + (int)keyTable.Length;

        // Data table: each value occupies its (padded) allocated size.
        var dataTable = new MemoryStream();
        var dataOffsets = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            dataOffsets[i] = (int)dataTable.Length;
            dataTable.Write(values[i], 0, values[i].Length);
            int padLen = maxLens[i] - values[i].Length;
            if (padLen > 0) dataTable.Write(new byte[padLen], 0, padLen);
        }

        var output = new MemoryStream();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); output.Write(b, 0, 4); }
        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); output.Write(b, 0, 2); }

        // Header.
        U32(SfoParser.Magic);
        U32(Version);
        U32((uint)keyTableStart);
        U32((uint)dataTableStart);
        U32((uint)entries.Count);

        // Index entries.
        for (int i = 0; i < entries.Count; i++)
        {
            U16((ushort)keyOffsets[i]);
            U16((ushort)entries[i].Format);
            U32((uint)values[i].Length); // data_len (used)
            U32((uint)maxLens[i]);        // data_max_len (allocated)
            U32((uint)dataOffsets[i]);    // data_offset
        }

        output.Write(keyTable.ToArray());
        output.Write(dataTable.ToArray());
        return output.ToArray();
    }

    private static byte[] EncodeValue(SfoEntry e)
    {
        if (e.Format == SfoFormat.Int32)
        {
            uint v = e.IntValue ?? (uint.TryParse(e.Value, out var parsed) ? parsed : 0u);
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            return b;
        }

        var text = Encoding.UTF8.GetBytes(e.Value);
        if (e.Format == SfoFormat.Utf8) // null-terminated
        {
            var withNull = new byte[text.Length + 1];
            text.CopyTo(withNull, 0);
            return withNull;
        }
        return text; // Utf8Special: no terminator
    }
}
