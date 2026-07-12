using System;
using System.Buffers.Binary;
using PkgLens.Core;
using PkgLens.Core.Self;
using Xunit;

namespace PkgLens.Core.Tests;

public class EbootPatcherTests
{
    /// <summary>A byte buffer with a sys_process_param block (magic 0x13BCC5F6) at <paramref name="at"/>.</summary>
    private static byte[] WithProcessParam(uint sdkVersion, int at = 0x20)
    {
        var buf = new byte[at + 0x40];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 0x00), 0x24);        // size
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 0x04), 0x13BCC5F6);  // magic
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 0x08), 0x00330000);  // version
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 0x0C), sdkVersion);  // sdk_version
        return buf;
    }

    [Fact]
    public void FindSdkVersion_ReadsFieldAndDecodesFirmware()
    {
        byte[] elf = WithProcessParam(0x00440001, at: 0x20);

        var v = EbootPatcher.FindSdkVersion(elf);

        Assert.NotNull(v);
        Assert.Equal(0x20 + 0x0C, v!.Offset);
        Assert.Equal(0x00440001u, v.Value);
        Assert.Equal("4.40", v.Display);
    }

    [Fact]
    public void FindSdkVersion_NoStruct_ReturnsNull() =>
        Assert.Null(EbootPatcher.FindSdkVersion(new byte[0x80]));

    [Fact]
    public void SetFirmwareVersion_LowersRequirement_PreservingLowBits()
    {
        byte[] elf = WithProcessParam(0x00440001);

        var prev = EbootPatcher.SetFirmwareVersion(elf, 4, 0); // 4.40 -> 4.00

        Assert.Equal("4.40", prev!.Display);
        Assert.Equal(0x00400001u, EbootPatcher.FindSdkVersion(elf)!.Value); // low 0x0001 kept
    }

    [Theory]
    [InlineData(4, 70, 0x00470001u)]
    [InlineData(3, 40, 0x00340001u)]
    public void SetFirmwareVersion_EncodesVersionByte(int major, int minor, uint expected)
    {
        byte[] elf = WithProcessParam(0x00990001);
        EbootPatcher.SetFirmwareVersion(elf, major, minor);
        Assert.Equal(expected, EbootPatcher.FindSdkVersion(elf)!.Value);
    }

    [Fact]
    public void SetSdkVersionRaw_WritesVerbatim()
    {
        byte[] elf = WithProcessParam(0x00440001);
        EbootPatcher.SetSdkVersionRaw(elf, 0x0155AA02);
        Assert.Equal(0x0155AA02u, EbootPatcher.FindSdkVersion(elf)!.Value);
    }

    [Fact]
    public void SetFirmwareVersion_NoStruct_ReturnsNull() =>
        Assert.Null(EbootPatcher.SetFirmwareVersion(new byte[0x40], 4, 0));

    [Fact]
    public void PatchPattern_ReplacesAllOccurrences_AndCounts()
    {
        var data = new byte[] { 0xAA, 0xBB, 0x01, 0x02, 0xAA, 0xBB, 0x03 };
        int n = EbootPatcher.PatchPattern(data, new byte[] { 0xAA, 0xBB }, new byte[] { 0xCC, 0xDD });
        Assert.Equal(2, n);
        Assert.Equal(new byte[] { 0xCC, 0xDD, 0x01, 0x02, 0xCC, 0xDD, 0x03 }, data);
    }

    [Fact]
    public void PatchPattern_FirstOnly_StopsAfterOne()
    {
        var data = new byte[] { 0x01, 0x01, 0x01 };
        int n = EbootPatcher.PatchPattern(data, new byte[] { 0x01 }, new byte[] { 0x09 }, firstOnly: true);
        Assert.Equal(1, n);
        Assert.Equal(new byte[] { 0x09, 0x01, 0x01 }, data);
    }

    [Fact]
    public void PatchPattern_LengthMismatch_Throws() =>
        Assert.Throws<ArgumentException>(() => EbootPatcher.PatchPattern(new byte[4], new byte[] { 1 }, new byte[] { 1, 2 }));

    [Fact]
    public void PatchAt_OutOfBounds_Throws() =>
        Assert.Throws<PkgFormatException>(() => EbootPatcher.PatchAt(new byte[4], 3, new byte[] { 1, 2 }));
}
