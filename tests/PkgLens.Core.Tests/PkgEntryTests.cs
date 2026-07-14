using System.Buffers.Binary;
using PkgLens.Core.Shared.Models;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>Unit coverage for the item-record flag decoding on <see cref="PkgEntry"/>.</summary>
public class PkgEntryTests
{
    private static PkgEntry Record(uint rawType)
    {
        var rec = new byte[PkgEntry.RecordSize];
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(0x18), rawType);
        return PkgEntry.ParseRecord(rec);
    }

    [Theory]
    [InlineData(0x00000003u, false)] // plain PS3 regular file
    [InlineData(0x10000003u, true)]  // PSP flag alone
    [InlineData(0x90000003u, true)]  // 0x90 marker = overwrite (0x80) | PSP (0x10)
    [InlineData(0x80000003u, false)] // overwrite flag, but not PSP
    public void IsPsp_ReflectsFlagBit(uint rawType, bool expected)
    {
        Assert.Equal(expected, Record(rawType).IsPsp);
    }

    [Fact]
    public void Kind_UsesLowByteOnly_IgnoringFlags()
    {
        // High-byte flags must not bleed into the kind.
        Assert.Equal(PkgEntryType.Folder, Record(0x90000000u | (uint)PkgEntryType.Folder).Kind);
    }
}
