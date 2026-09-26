using System;
using System.IO;
using System.Threading;
using PkgLens.Core;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;

namespace PkgLens.Gui.Services;

/// <summary>Builds separate SELF outputs from ELF or SELF inputs, verifying before atomic publication.</summary>
internal static class SelfBuildService
{
    internal static long BuildFile(string input, string destination, bool encrypted,
        SelfBuilder.FakeSelfOptions metadata, ushort revision, EdatKeySelection inputKey,
        EdatKeySelection outputKey, string? rapDirectory, CancellationToken token = default, bool signHeader = false,
        bool overwrite = true)
    {
        if (signHeader && !encrypted)
            throw new PkgFormatException("Legacy signing requires encrypted SELF output.");
        AtomicOutput.EnsureDifferentPath(input, destination);
        token.ThrowIfCancellationRequested();
        byte[] source;
        using (var file = File.OpenRead(input))
        {
            if (file.Length > 128 * 1024 * 1024) throw new PkgFormatException("SELF building supports inputs up to 128 MiB.");
            source = new byte[(int)file.Length];
            file.ReadExactly(source);
        }
        byte[] elf = source;
        // Work on a copy so metadata inferred from one input cannot leak into the next operation.
        var m = new SelfBuilder.FakeSelfOptions
        {
            Npdrm = metadata.Npdrm, AuthId = metadata.AuthId, VendorId = metadata.VendorId,
            AppVersion = metadata.AppVersion, ProgramType = metadata.ProgramType, ContentId = metadata.ContentId,
            NpLicenseType = metadata.NpLicenseType, NpAppType = metadata.NpAppType,
            FirmwareVersion = metadata.FirmwareVersion, ControlFlags = metadata.ControlFlags,
            CompressSegments = metadata.CompressSegments,
        };
        if (source.AsSpan().StartsWith("SCE\0"u8))
        {
            SelfInfo info = SelfReader.ParseInfo(new MemoryStream(source));
            byte[]? key = info.KeyRevision is not 0x8000 and not 0xC000 && info.Npdrm is { } npd
                ? ResolveKey(npd.ContentId, Path.GetFileName(input), npd.RawLicenseType, inputKey, rapDirectory)
                : null;
            elf = SelfDecryptor.Decrypt(source, key).Elf;
            m.AuthId ??= info.AuthId; m.VendorId ??= info.VendorId; m.AppVersion ??= info.SdkVersion;
            m.ControlFlags ??= info.ControlFlags; m.FirmwareVersion ??= info.FirmwareVersion;
            if (m.Npdrm && info.Npdrm is { } original)
            {
                m.ContentId ??= original.ContentId;
                m.NpAppType ??= original.AppType;
            }
        }
        token.ThrowIfCancellationRequested();
        if (m.Npdrm && m.NpAppType is null && elf.Length >= 0x12)
            m.NpAppType = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(elf.AsSpan(0x10)) == 0xFFA4 ? 0u : 1u;
        byte[] result;
        if (encrypted)
        {
            byte[]? key = m.Npdrm
                ? ResolveKey(m.ContentId ?? "", Path.GetFileName(destination), m.NpLicenseType ?? 3, outputKey, rapDirectory)
                : null;
            result = EncryptedSelfBuilder.Build(elf, new()
            { Metadata = m, KeyRevision = revision, Klicensee = key, FileName = Path.GetFileName(destination), SignHeader = signHeader }, token);
        }
        else
        {
            result = SelfBuilder.MakeFakeSelf(elf, m);
            if (!SelfDecryptor.Decrypt(result).Elf.AsSpan().SequenceEqual(elf))
                throw new PkgFormatException("fSELF verification failed: recovered ELF differs from the source.");
        }
        token.ThrowIfCancellationRequested();
        AtomicOutput.Write(destination, stream => { stream.Write(result); token.ThrowIfCancellationRequested(); }, overwrite);
        return result.LongLength;
    }

    internal static long DecryptFile(string input, string destination, EdatKeySelection selection,
        string? rapDirectory, CancellationToken token = default, bool overwrite = true)
    {
        AtomicOutput.EnsureDifferentPath(input, destination);
        token.ThrowIfCancellationRequested();
        using var file = File.OpenRead(input);
        if (file.Length > 128 * 1024 * 1024)
            throw new PkgFormatException("SELF decryption supports inputs up to 128 MiB.");
        byte[] source = new byte[(int)file.Length];
        file.ReadExactly(source);
        SelfInfo info = SelfReader.ParseInfo(new MemoryStream(source));
        byte[]? key = info.KeyRevision is not 0x8000 and not 0xC000 && info.Npdrm is { } np
            ? ResolveKey(np.ContentId, Path.GetFileName(input), np.RawLicenseType, selection, rapDirectory) : null;
        byte[] elf = SelfDecryptor.Decrypt(source, key).Elf;
        // Validate the recovered ELF with the existing builder/parser before publishing it.
        byte[] check = SelfBuilder.MakeFakeSelf(elf, npdrm: false);
        if (!SelfDecryptor.Decrypt(check).Elf.AsSpan().SequenceEqual(elf))
            throw new PkgFormatException("Recovered ELF validation failed.");
        token.ThrowIfCancellationRequested();
        AtomicOutput.Write(destination, stream => { stream.Write(elf); token.ThrowIfCancellationRequested(); }, overwrite);
        return elf.LongLength;
    }

    private static byte[]? ResolveKey(string contentId, string fileName, uint license,
        EdatKeySelection selection, string? rapDirectory)
        => FindKey(contentId, fileName, license, selection, rapDirectory)
            ?? throw new PkgFormatException("No matching NPDRM key found. Select the matching RAP or enter a raw klicensee.");

    internal static byte[]? FindKey(string contentId, string fileName, uint license,
        EdatKeySelection selection, string? rapDirectory)
    {
        if (EdatWorkbenchService.ParseKey(selection.RawKey) is { } raw) return raw;
        if (!string.IsNullOrWhiteSpace(selection.RapPath))
        {
            using var rap = File.OpenRead(selection.RapPath);
            if (rap.Length != 16) throw new PkgFormatException("A RAP file must be exactly 16 bytes.");
            byte[] data = new byte[16]; rap.ReadExactly(data);
            return NpdKeys.RapToKlicensee(data);
        }
        // SELF free licenses use the SELF constant, not EDAT's different free developer key.
        if (KlicenseeStore.ExtractTitleId(contentId) is not null)
        {
            var stored = KlicenseeStore.Find(contentId, fileName, license)
                ?? KnownKlicenseeStore.Find(contentId, fileName, license);
            if (stored is not null) return stored.Klicensee;
        }
        if (license == 3) return SelfKeyset.NpKlicFree;
        byte[]? found = RapStore.Find(contentId, rapDirectory);
        if (found is not null) return NpdKeys.RapToKlicensee(found);
        return null;
    }
}
