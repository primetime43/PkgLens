namespace PkgLens.Core.Shared.Models;

/// <summary>
/// Whether a package is finalized (retail) or non-finalized (debug). Stored at header
/// offset 0x04. Retail packages use AES-128-CTR; debug packages use a SHA-1 keystream.
/// </summary>
public enum PkgFinalization
{
    /// <summary>0x8000 — finalized / retail. Decrypts with AES-128-CTR + the PS3 gpkg key.</summary>
    Retail,

    /// <summary>0x0000 — non-finalized / debug. Decrypts with a self-contained SHA-1 keystream.</summary>
    Debug,

    /// <summary>Any other value seen at offset 0x04.</summary>
    Unknown,
}

/// <summary>
/// Target platform of the package. Stored at header offset 0x06.
/// </summary>
public enum PkgPlatform
{
    /// <summary>0x0001 — PS3.</summary>
    Ps3,

    /// <summary>0x0002 — PSP / PSVita.</summary>
    PspPsVita,

    /// <summary>Any other value seen at offset 0x06.</summary>
    Unknown,
}

/// <summary>
/// The "type" field of a PKG item record (offset 0x18 within a 0x20-byte entry). The low
/// byte carries the entry kind; the high bits carry flags. Values mirror RPCS3's unpkg.h.
/// </summary>
public enum PkgEntryType
{
    /// <summary>1 — NPDRM content (e.g. EBOOT.BIN / self).</summary>
    Npdrm = 1,

    /// <summary>2 — NPDRM EDAT.</summary>
    NpdrmEdat = 2,

    /// <summary>3 — a regular file.</summary>
    Regular = 3,

    /// <summary>4 — a directory.</summary>
    Folder = 4,
}
