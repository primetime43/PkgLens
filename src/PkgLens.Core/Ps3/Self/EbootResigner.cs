using System.Buffers.Binary;

namespace PkgLens.Core.Ps3.Self;

/// <summary>What happened when an EBOOT was resigned.</summary>
public enum EbootResignAction
{
    /// <summary>Already a fake-signed SELF (key revision 0x8000) — left unchanged.</summary>
    AlreadyFakeSigned,
    /// <summary>Input was a plaintext ELF; fake-signed directly.</summary>
    ResignedFromElf,
    /// <summary>Input was an encrypted SELF; decrypted then fake-signed.</summary>
    DecryptedAndResigned,
}

/// <summary>The resigned bytes plus a record of how they were produced.</summary>
public sealed record EbootResignResult(byte[] Data, EbootResignAction Action, bool Npdrm, string? ContentId);

/// <summary>
/// Turns an <c>EBOOT.BIN</c> / SELF / ELF into a fake-signed SELF (fSELF) that boots on a jailbroken
/// (CFW) PS3 without its original license — the "Resign EBOOT" operation. It combines the decrypt
/// (<see cref="SelfDecryptor"/>) and fake-sign (<see cref="SelfBuilder"/>) steps: an encrypted retail
/// SELF is decrypted to an ELF and then re-signed keyless; a plaintext ELF is signed directly; an
/// already-fSELF is passed through untouched. No signing keys are used.
/// </summary>
public static class EbootResigner
{
    private const uint SceMagic = 0x53434500;
    private const uint ElfMagic = 0x7F454C46;

    /// <summary>
    /// Resigns <paramref name="eboot"/> (raw EBOOT.BIN / .self / .elf bytes) to a fSELF.
    /// </summary>
    /// <param name="klicensee">
    /// Optional 16-byte NPDRM klicensee (from a RAP) needed only when the input is an <em>encrypted,
    /// licensed</em> SELF. Free-license and debug SELFs, and plaintext ELFs, need none.
    /// </param>
    public static EbootResignResult Resign(byte[] eboot, byte[]? klicensee = null)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        if (eboot.Length < 4)
            throw new PkgFormatException("EBOOT is too small to be an ELF or SELF.");

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(eboot);

        if (magic == ElfMagic)
        {
            // A decrypted ELF — fake-sign it. Assume NON-DRM unless the caller knows otherwise.
            byte[] fself = SelfBuilder.MakeFakeSelf(eboot, npdrm: false);
            return new EbootResignResult(fself, EbootResignAction.ResignedFromElf, Npdrm: false, ContentId: null);
        }

        if (magic != SceMagic)
            throw new PkgFormatException("EBOOT is neither an ELF nor a SELF (missing ELF / SCE magic).");

        // A SELF. If it is already fake-signed, leave it as-is.
        SelfInfo info = SelfReader.ParseInfo(new MemoryStream(eboot, writable: false));
        if (info.IsLikelyFakeSigned)
            return new EbootResignResult(eboot, EbootResignAction.AlreadyFakeSigned, info.IsNpdrm, info.Npdrm?.ContentId);

        // A real signed SELF — decrypt it, then fake-sign the recovered ELF.
        SelfDecryptResult dec = SelfDecryptor.Decrypt(eboot, klicensee);
        byte[] resigned = SelfBuilder.MakeFakeSelf(dec.Elf, npdrm: dec.WasNpdrm);
        return new EbootResignResult(resigned, EbootResignAction.DecryptedAndResigned, dec.WasNpdrm, dec.ContentId);
    }
}
