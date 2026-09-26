using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using PkgLens.Core;
using PkgLens.Core.Ps3.Self;

namespace PkgLens.Gui.Services;

internal sealed record SelfVerificationCheck(string Status, string Detail);
internal sealed record SelfVerificationReport(SelfInfo Info, SelfVerificationCheck Signature, SelfVerificationCheck Decryption)
{
    public SelfTargetInfo Target => SelfTargetInfo.FromInfo(Info);
    public string TargetDetail => Target.Format == SelfTargetFormat.CexRetail
        ? "Retail SELF header format. Signature status is shown separately above; console compatibility is not verified."
        : Target.Detail;
    public const string Scope = "Checks the supported legacy header signature and whether an ELF can be recovered. "
        + "Payload hashes, NPDRM footer signatures, licenses and console compatibility are not verified.";
}

/// <summary>Read-only checks on existing SELF files. Keeps signature and decryption outcomes independent.</summary>
internal static class SelfVerificationService
{
    internal const int MaxInputBytes = 128 * 1024 * 1024;

    internal static SelfVerificationReport VerifyFile(string path, EdatKeySelection selection,
        string? rapDirectory = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxInputBytes)
            throw new PkgFormatException("SELF verification supports files up to 128 MiB.");
        byte[] bytes = new byte[(int)stream.Length];
        for (int offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(1024 * 1024, bytes.Length - offset);
            stream.ReadExactly(bytes.AsSpan(offset, count));
            offset += count;
        }
        return Verify(bytes, Path.GetFileName(path), selection, rapDirectory, token);
    }

    internal static SelfVerificationReport Verify(byte[] bytes, string fileName, EdatKeySelection selection,
        string? rapDirectory = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaxInputBytes) throw new PkgFormatException("SELF verification supports files up to 128 MiB.");
        SelfInfo info = SelfReader.ParseInfo(new MemoryStream(bytes, writable: false));
        if (info.HeaderVersion != 2)
        {
            var unsupported = new SelfVerificationCheck("Unsupported", "This SCE header version is not supported for PS3 verification.");
            return new(info, unsupported, unsupported);
        }
        bool debug = info.KeyRevision is 0x8000 or 0xC000;
        bool supportedSignature = LegacySelfSigning.IsSupported(info.RawProgramType, info.KeyRevision);
        bool supportedDecryption = debug || SelfKeyset.Find(info.RawProgramType, info.KeyRevision) is not null;
        byte[]? key = null;
        SelfVerificationCheck? keyProblem = null;
        if (!debug && info.RawProgramType == 8 && (supportedSignature || supportedDecryption))
        {
            if (info.Npdrm is not { } np)
                keyProblem = new("Failed", "NPDRM license metadata is missing or unreadable.");
            else
            {
                try
                {
                    key = SelfBuildService.FindKey(np.ContentId, fileName, np.RawLicenseType, selection, rapDirectory);
                    if (key is null) keyProblem = new("Missing key", "No matching NPDRM key found. Select a RAP or enter the matching klicensee, then verify again.");
                }
                catch (Exception ex) when (IsFileError(ex))
                { keyProblem = new("Key error", ex.Message); }
            }
        }

        token.ThrowIfCancellationRequested();
        SelfVerificationCheck signature;
        if (debug) signature = new("Unsigned", "Debug / fake-signed SELF has no retail header signature to verify.");
        else if (!supportedSignature) signature = new("Unsupported", "No bundled public signing profile for this program type and key revision.");
        else if (keyProblem is not null) signature = keyProblem;
        else
        {
            try
            {
                signature = SelfSignature.VerifyHeader(bytes, key) switch
                {
                    SelfSignatureStatus.Valid => new("Valid", "Header signature matches the supported legacy public key."),
                    SelfSignatureStatus.Invalid => new("Invalid", "Header signature does not match the signed header contents."),
                    SelfSignatureStatus.Absent => new("Unsigned", "The header signature is empty."),
                    _ => new("Unsupported", "No bundled public signing profile for this file."),
                };
            }
            catch (Exception ex) when (IsFileError(ex))
            { signature = new("Failed", "Could not check the header signature. " + ex.Message); }
        }

        token.ThrowIfCancellationRequested();
        SelfVerificationCheck decryption;
        if (!supportedDecryption) decryption = new("Unsupported", "No bundled decryption key for this program type and key revision.");
        else if (keyProblem is not null) decryption = keyProblem;
        else if (info.Elf is null) decryption = new("Failed", "The embedded ELF header is missing or unreadable.");
        else if (info.Elf is { Is64Bit: false } or { IsBigEndian: false } || info.Elf?.Machine != 21)
            decryption = new("Unsupported", "Decryption checks currently support 64-bit, big-endian PS3 PPU executables.");
        else
        {
            try
            {
                byte[] elf = SelfDecryptor.Decrypt(bytes, key).Elf;
                token.ThrowIfCancellationRequested();
                // Reuse the existing ELF structural validation used before publishing decrypted files.
                _ = SelfBuilder.MakeFakeSelf(elf, new SelfBuilder.FakeSelfOptions { CompressSegments = false });
                decryption = new("Succeeded", $"Recovered {elf.LongLength:n0} bytes; ELF structure accepted. Payload integrity has not been authenticated.");
            }
            catch (Exception ex) when (IsFileError(ex))
            { decryption = new("Failed", "Could not recover a supported ELF. The key may be incorrect or the file may be damaged. " + ex.Message); }
        }
        token.ThrowIfCancellationRequested();
        return new(info, signature, decryption);
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or PkgFormatException or CryptographicException or ArgumentException or InvalidOperationException
        or OverflowException or NotSupportedException;
}
