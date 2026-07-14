using System.Buffers.Binary;
using System.Linq;
using PkgLens.Core.Shared.Sfo;
using PkgLens.Core.Tests.TestData;
using Xunit;

namespace PkgLens.Core.Tests;

public class SfoWriterTests
{
    [Fact]
    public void Parse_HostileDataMaxLen_ClampedAndRoundTripStaysSmall()
    {
        // A malformed data_max_len (0x7FFFFFFF) must not drive the writer to pad a ~2 GB field.
        byte[] blob = new SfoBuilder().AddString("TITLE", "Hello").Build();
        // Patch entry 0's data_max_len (index entry 0 at 0x14, field at +0x08).
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x14 + 0x08), 0x7FFFFFFF);

        var table = SfoParser.Parse(blob);
        Assert.True(table.Entries[0].MaxLength <= (uint)blob.Length); // clamped to the blob size

        byte[] rewritten = SfoWriter.Write(table.Entries);
        Assert.True(rewritten.Length < 0x10000); // no multi-GB allocation
        Assert.Equal("Hello", SfoParser.Parse(rewritten).Title);
    }

    [Fact]
    public void Write_RoundTripsBinarySpecialValue_ByteForByte()
    {
        // A Utf8Special value holding non-UTF-8 bytes must survive parse→write unchanged; the lossy
        // UTF-8 Value view would corrupt it (U+FFFD), so the writer must emit RawValue verbatim.
        byte[] binary = { 0x00, 0xFF, 0xC0, 0x80, 0x01, 0xFE, 0x7F };
        byte[] blob = new SfoBuilder().AddString("TITLE", "T").AddSpecial("DATA", binary).Build();

        var table = SfoParser.Parse(blob);
        byte[] rewritten = SfoWriter.Write(table.Entries);
        var reparsed = SfoParser.Parse(rewritten);

        Assert.Equal(binary, reparsed.Entries.Single(e => e.Key == "DATA").RawValue);
    }

    [Fact]
    public void Write_KeyTableExceedingU16Offset_ThrowsInsteadOfTruncating()
    {
        // A key offset beyond 65535 would silently wrap to a u16 and corrupt the index.
        var entries = new[]
        {
            new SfoEntry { Key = new string('A', 70000), Format = SfoFormat.Utf8, Value = "x" },
            new SfoEntry { Key = "B", Format = SfoFormat.Utf8, Value = "y" },
        };
        Assert.Throws<PkgLens.Core.PkgFormatException>(() => SfoWriter.Write(entries));
    }

    [Fact]
    public void Parse_ValueOffsetOverflow_ThrowsInsteadOfReadingWrongBytes()
    {
        // dataTableStart + dataOffset must be widened before the bounds check, or a huge dataOffset
        // wraps mod 2^32 and reads the wrong region as the value. Patch entry 0's data_offset.
        byte[] blob = new SfoBuilder().AddString("TITLE", "Hello").Build();
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x14 + 0x0C), 0xFFFFF000);

        Assert.Throws<PkgLens.Core.PkgFormatException>(() => SfoParser.Parse(blob));
    }

    [Fact]
    public void Write_RoundTripsUnchangedTable()
    {
        byte[] blob = new SfoBuilder()
            .AddString("TITLE", "Original Title")
            .AddString("TITLE_ID", "NPUB30910")
            .AddInt("PARENTAL_LEVEL", 5)
            .Build();

        var table = SfoParser.Parse(blob);
        byte[] rewritten = SfoWriter.Write(table.Entries);
        var reparsed = SfoParser.Parse(rewritten);

        Assert.Equal("Original Title", reparsed.Title);
        Assert.Equal("NPUB30910", reparsed.TitleId);
        Assert.Equal(5u, reparsed.ParentalLevel);
    }

    [Fact]
    public void Write_AppliesEditedStringAndInt()
    {
        byte[] blob = new SfoBuilder()
            .AddString("TITLE", "Old")
            .AddInt("PARENTAL_LEVEL", 5)
            .Build();
        var table = SfoParser.Parse(blob);

        var edited = table.Entries
            .Select(e => e.Key switch
            {
                "TITLE" => e.WithValue("A Much Longer New Title"),
                "PARENTAL_LEVEL" => e.WithInt(11),
                _ => e,
            })
            .ToList();

        var reparsed = SfoParser.Parse(SfoWriter.Write(edited));
        Assert.Equal("A Much Longer New Title", reparsed.Title);
        Assert.Equal(11u, reparsed.ParentalLevel);
    }

    [Fact]
    public void Write_PreservesEntryOrderAndFormats()
    {
        byte[] blob = new SfoBuilder().AddString("A", "x").AddInt("B", 1).AddString("C", "y").Build();
        var table = SfoParser.Parse(blob);

        var reparsed = SfoParser.Parse(SfoWriter.Write(table.Entries));
        Assert.Equal(new[] { "A", "B", "C" }, reparsed.Entries.Select(e => e.Key));
        Assert.Equal(SfoFormat.Int32, reparsed.Entries[1].Format);
    }
}
