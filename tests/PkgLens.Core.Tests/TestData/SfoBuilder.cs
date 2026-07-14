using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Sfo;

namespace PkgLens.Core.Tests.TestData;

/// <summary>Builds a valid little-endian PARAM.SFO blob in-memory for tests.</summary>
public sealed class SfoBuilder
{
    private readonly List<(string Key, SfoFormat Fmt, byte[] Data)> _entries = new();

    public SfoBuilder AddString(string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0"); // null-terminated
        _entries.Add((key, SfoFormat.Utf8, bytes));
        return this;
    }

    public SfoBuilder AddInt(string key, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _entries.Add((key, SfoFormat.Int32, bytes));
        return this;
    }

    /// <summary>Adds a raw Utf8Special (0x0004) value verbatim — used to test binary round-tripping.</summary>
    public SfoBuilder AddSpecial(string key, byte[] raw)
    {
        _entries.Add((key, SfoFormat.Utf8Special, raw));
        return this;
    }

    public byte[] Build()
    {
        const int headerSize = 0x14;
        int indexSize = _entries.Count * 0x10;

        // Key table (null-terminated keys) and per-entry key offsets.
        var keyTable = new MemoryStream();
        var keyOffsets = new List<int>();
        foreach (var e in _entries)
        {
            keyOffsets.Add((int)keyTable.Length);
            var kb = Encoding.UTF8.GetBytes(e.Key + "\0");
            keyTable.Write(kb, 0, kb.Length);
        }
        AlignTo(keyTable, 4);

        int keyTableStart = headerSize + indexSize;
        int dataTableStart = keyTableStart + (int)keyTable.Length;

        // Data table and per-entry data offsets.
        var dataTable = new MemoryStream();
        var dataOffsets = new List<int>();
        foreach (var e in _entries)
        {
            dataOffsets.Add((int)dataTable.Length);
            dataTable.Write(e.Data, 0, e.Data.Length);
        }

        var output = new MemoryStream();

        void W32(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            output.Write(b, 0, 4);
        }
        void W16(ushort v)
        {
            var b = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            output.Write(b, 0, 2);
        }

        // Header.
        W32(SfoParser.Magic);
        W32(0x00000101); // version 1.1
        W32((uint)keyTableStart);
        W32((uint)dataTableStart);
        W32((uint)_entries.Count);

        // Index entries.
        for (int i = 0; i < _entries.Count; i++)
        {
            W16((ushort)keyOffsets[i]);
            W16((ushort)_entries[i].Fmt);
            W32((uint)_entries[i].Data.Length);      // data_len
            W32((uint)_entries[i].Data.Length);      // data_max_len
            W32((uint)dataOffsets[i]);               // data_offset
        }

        output.Write(keyTable.ToArray());
        output.Write(dataTable.ToArray());
        return output.ToArray();
    }

    private static void AlignTo(MemoryStream s, int alignment)
    {
        while (s.Length % alignment != 0) s.WriteByte(0);
    }
}
