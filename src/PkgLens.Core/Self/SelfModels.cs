namespace PkgLens.Core.Self;

/// <summary>The kind of SCE file (SCE header <c>header_type</c> at 0x0A).</summary>
public enum SceCategory : ushort
{
    Self = 1,
    Rvk = 2,
    Pkg = 3,
    Spp = 4,
}

/// <summary>
/// The program type of a SELF (from <c>app_info.self_type</c>). Determines which key set and
/// signing scheme a resigner must use.
/// </summary>
public enum SelfProgramType : uint
{
    Lv0 = 1,
    Lv1 = 2,
    Lv2 = 3,
    Application = 4,
    IsolatedSpuModule = 5,
    SecureLoader = 6,
    Unknown7 = 7,
    Npdrm = 8,
}

/// <summary>NPDRM license (DRM) type from the NPDRM control-info block.</summary>
public enum NpdrmLicenseType : uint
{
    Network = 1,
    Local = 2,
    Free = 3,
}

/// <summary>The NPDRM control-info block ("NPD\0"): the DRM metadata carried inside an NPDRM SELF.</summary>
public sealed class SelfNpdrmInfo
{
    public uint Version { get; init; }
    public uint RawLicenseType { get; init; }
    public uint AppType { get; init; }
    public string ContentId { get; init; } = string.Empty;

    public NpdrmLicenseType? LicenseType =>
        Enum.IsDefined(typeof(NpdrmLicenseType), RawLicenseType) ? (NpdrmLicenseType)RawLicenseType : null;

    public string LicenseText => LicenseType?.ToString() ?? $"0x{RawLicenseType:X}";
}

/// <summary>Fields decoded from the plaintext ELF header embedded in the SELF.</summary>
public sealed class ElfIdent
{
    public bool Is64Bit { get; init; }
    public bool IsBigEndian { get; init; }
    public ushort Type { get; init; }      // e_type (2 = EXEC, 3 = DYN, 0xFFA0 = PS3 PRX)
    public ushort Machine { get; init; }   // e_machine (0x15 = PPC64)

    public string TypeText => Type switch
    {
        2 => "EXEC (executable)",
        3 => "DYN (shared object / PRX)",
        0xFFA0 => "PS3 PRX",
        _ => $"0x{Type:X}",
    };
}

/// <summary>
/// The parsed, non-decrypted view of a SELF (Signed ELF) such as <c>EBOOT.BIN</c>, <c>.self</c> or
/// <c>.sprx</c>. Header, program type, the embedded ELF header, and control info (including NPDRM)
/// are read directly — no keys required. Decrypting the code segments is a separate step.
/// </summary>
public sealed class SelfInfo
{
    public long FileSize { get; init; }

    // SCE header.
    public uint HeaderVersion { get; init; }
    public ushort KeyRevision { get; init; }
    public SceCategory Category { get; init; }
    public uint MetadataOffset { get; init; }
    public ulong HeaderLength { get; init; }
    public ulong DataLength { get; init; }

    // app_info.
    public ulong AuthId { get; init; }
    public uint VendorId { get; init; }
    public uint RawProgramType { get; init; }
    public ulong SdkVersion { get; init; }

    public ElfIdent? Elf { get; init; }
    public SelfNpdrmInfo? Npdrm { get; init; }

    public SelfProgramType? ProgramType =>
        Enum.IsDefined(typeof(SelfProgramType), RawProgramType) ? (SelfProgramType)RawProgramType : null;

    public string ProgramTypeText => ProgramType?.ToString() ?? $"0x{RawProgramType:X}";

    public bool IsNpdrm => ProgramType == SelfProgramType.Npdrm || Npdrm is not null;

    /// <summary>
    /// A fake-signed (fSELF) SELF is flagged by key revision 0x8000 — the marker CFW-oriented tools
    /// (make_fself) set so the loader takes the "no real signature" path. A best-effort hint.
    /// </summary>
    public bool IsLikelyFakeSigned => KeyRevision == 0x8000;

    /// <summary>The raw <c>app_info.version</c> field, shown as hex (its exact meaning varies by SELF).</summary>
    public string VersionText => $"0x{SdkVersion:X16}";
}
