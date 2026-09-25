using System.Buffers.Binary;

namespace PkgLens.Core.Ps3.Self;

public enum SelfTargetFormat { Unknown, CexRetail, DexDebug }

/// <summary>A header-based format hint, not a signature check or a console compatibility test.</summary>
public sealed record SelfTargetInfo(SelfTargetFormat Format, string Detail)
{
    public string Label => Format switch
    {
        SelfTargetFormat.CexRetail => "CEX / retail",
        SelfTargetFormat.DexDebug => "DEX / debug (fSELF)",
        _ => "Unknown",
    };

    public static SelfTargetInfo PlainElf { get; } = Unknown("Plain ELF has no SELF signing header to identify CEX or DEX.");
    public static SelfTargetInfo Unknown(string reason) => new(SelfTargetFormat.Unknown, reason);

    public static SelfTargetInfo FromInfo(SelfInfo info) =>
        info.HeaderVersion != 2 || info.Category != SceCategory.Self
            ? Unknown("Unrecognized PS3 SELF header.")
            : Classify(info.KeyRevision, info.RawProgramType);

    /// <summary>
    /// Inspects a bounded prefix without decrypting the executable. Null means neither SELF nor ELF.
    /// Missing app-info bytes yield Unknown; callers need not load the whole file.
    /// </summary>
    public static SelfTargetInfo? ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith("\x7f"u8) && header.Length >= 4 && header.Slice(1, 3).SequenceEqual("ELF"u8))
            return PlainElf;
        if (!header.StartsWith("SCE\0"u8)) return null;
        if (header.Length < 0x20 || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != 2
            || BinaryPrimitives.ReadUInt16BigEndian(header[10..]) != 1)
            return Unknown("Incomplete or unrecognized PS3 SELF header.");
        ushort revision = BinaryPrimitives.ReadUInt16BigEndian(header[8..]);
        if (revision is 0x8000 or 0xC000) return Classify(revision, 0);
        if (header.Length < 0x70) return Unknown("Incomplete SELF application information.");
        ulong offset = BinaryPrimitives.ReadUInt64BigEndian(header[0x28..]);
        if (offset < 0x70 || offset > (ulong)(header.Length - 0x20))
            return Unknown("SELF application information is outside the inspected header.");
        uint programType = BinaryPrimitives.ReadUInt32BigEndian(header[((int)offset + 12)..]);
        return Classify(revision, programType);
    }

    private static SelfTargetInfo Classify(ushort revision, uint programType)
    {
        // RPCS3 CheckDebugSelf recognizes both fSELF markers. Debug format is also used by CFW tools.
        if (revision is 0x8000 or 0xC000)
            return new(SelfTargetFormat.DexDebug,
                $"Debug SELF marker 0x{revision:X4}. This format can also run on CEX with compatible CFW; it does not mean DEX-only.");
        // Limit the retail inference to APP/NPDRM revisions with a known retail key mapping.
        // System SELF revisions and unknown keys do not reliably establish a target here.
        if (SelfKeyset.Find(programType, revision) is not null)
            return new(SelfTargetFormat.CexRetail,
                $"Known retail {(programType == 8 ? "NPDRM" : "APP")} key revision 0x{revision:X4}. Header format only; signature validity and console compatibility are not verified.");
        return Unknown($"No CEX/DEX classification for program type 0x{programType:X}, key revision 0x{revision:X4}.");
    }
}
