using System.Buffers.Binary;

namespace PkgLens.Core.Ps3.Self;

/// <summary>The firmware SDK version an ELF requires, found in its <c>sys_process_param</c> structure.</summary>
public sealed record SdkVersion(int Offset, uint Value)
{
    /// <summary>
    /// The version byte (bits 16–23), e.g. 0x44 for firmware 4.4x. High nibble = major, low nibble =
    /// tens of minor. The minor's ones digit lives in the high nibble of the next byte (bits 12–15),
    /// so 0x00446001 = 4.46 and 0x00440001 = 4.40.
    /// </summary>
    public byte VersionByte => (byte)((Value >> 16) & 0xFF);

    private int MinorOnes => (int)((Value >> 12) & 0xF);

    /// <summary>A friendly firmware string, e.g. "4.46" — both minor digits decoded, not just the tens.</summary>
    public string Display => $"{VersionByte >> 4}.{VersionByte & 0xF}{MinorOnes}";
}

/// <summary>
/// Applies "magic patches" to a decrypted PS3 executable (ELF) — small static edits that make a game
/// boot on CFW when a plain fake-sign isn't enough. The flagship patch lowers the <b>firmware / SDK
/// version</b> the executable demands (stored in its <c>sys_process_param</c> block, magic
/// <c>0x13BCC5F6</c>), so a title built against a newer SDK runs on an older CFW. Generic offset and
/// find/replace patches are provided for any other known byte edits. Operates on a plaintext ELF; an
/// encrypted EBOOT must be decrypted first (<see cref="SelfDecryptor"/>) and re-signed after
/// (<see cref="SelfBuilder"/>).
/// </summary>
public static class EbootPatcher
{
    /// <summary>Magic of the <c>sys_process_param</c> structure that carries the SDK version.</summary>
    private const uint ProcessParamMagic = 0x13BCC5F6;

    /// <summary>
    /// Locates the required firmware SDK version in <paramref name="elf"/> (its
    /// <c>sys_process_param.sdk_version</c>), or null if the structure isn't present.
    /// </summary>
    public static SdkVersion? FindSdkVersion(byte[] elf)
    {
        ArgumentNullException.ThrowIfNull(elf);
        int magic = FindMagic(elf, ProcessParamMagic);
        if (magic < 0) return null;

        // Layout: [size @-0x04][magic @0x00][version @+0x04][sdk_version @+0x08].
        int sdkOffset = magic + 0x08;
        if (sdkOffset + 4 > elf.Length) return null;
        return new SdkVersion(sdkOffset, BinaryPrimitives.ReadUInt32BigEndian(elf.AsSpan(sdkOffset)));
    }

    /// <summary>
    /// Sets the required firmware to <paramref name="major"/>.<paramref name="minor"/> (e.g. 4, 46 → "4.46").
    /// Encodes both minor digits: the tens into the version byte's low nibble (bits 16–19) and the ones
    /// into the next byte's high nibble (bits 12–15); all other bits (revision etc.) are preserved.
    /// Returns the previous version, or null if the ELF has no <c>sys_process_param</c> to patch.
    /// </summary>
    public static SdkVersion? SetFirmwareVersion(byte[] elf, int major, int minor)
    {
        if (major is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(major));
        if (minor is < 0 or > 99) throw new ArgumentOutOfRangeException(nameof(minor));

        var current = FindSdkVersion(elf);
        if (current is null) return null;

        int tens = minor / 10, ones = minor % 10;
        byte versionByte = (byte)((major << 4) | tens);
        // Clear bits 12–23 (version byte + minor-ones nibble), keep everything else.
        uint patched = (current.Value & 0xFF000FFFu) | ((uint)versionByte << 16) | ((uint)ones << 12);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(current.Offset), patched);
        return current;
    }

    /// <summary>Sets the raw 32-bit <c>sdk_version</c> value verbatim. Returns the previous version, or null.</summary>
    public static SdkVersion? SetSdkVersionRaw(byte[] elf, uint value)
    {
        var current = FindSdkVersion(elf);
        if (current is null) return null;
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(current.Offset), value);
        return current;
    }

    /// <summary>Overwrites <paramref name="bytes"/> at <paramref name="offset"/>. Returns the byte count written.</summary>
    public static int PatchAt(byte[] data, int offset, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(bytes);
        if (offset < 0 || offset + bytes.Length > data.Length)
            throw new PkgFormatException($"Offset patch [0x{offset:X}, +0x{bytes.Length:X}) is out of bounds.");
        bytes.CopyTo(data, offset);
        return bytes.Length;
    }

    /// <summary>
    /// Replaces occurrences of <paramref name="find"/> with <paramref name="replace"/> (same length).
    /// Returns the number of occurrences replaced (all, or just the first if <paramref name="firstOnly"/>).
    /// </summary>
    public static int PatchPattern(byte[] data, byte[] find, byte[] replace, bool firstOnly = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (find is null || find.Length == 0) throw new ArgumentException("find pattern is required.", nameof(find));
        if (replace is null || replace.Length != find.Length)
            throw new ArgumentException("replace must be the same length as find.", nameof(replace));

        int count = 0;
        int i = 0;
        while (i <= data.Length - find.Length)
        {
            if (Matches(data, i, find))
            {
                replace.CopyTo(data, i);
                count++;
                if (firstOnly) break;
                i += find.Length;
            }
            else i++;
        }
        return count;
    }

    private static int FindMagic(byte[] data, uint magic)
    {
        for (int i = 0; i + 4 <= data.Length; i++)
            if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i)) == magic)
                return i;
        return -1;
    }

    private static bool Matches(byte[] data, int at, byte[] pattern)
    {
        for (int j = 0; j < pattern.Length; j++)
            if (data[at + j] != pattern[j]) return false;
        return true;
    }
}
