using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using PkgLens.Core;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Gui.Services;

public enum KeyCoverageStatus { Verified, Opens, NoKeyNeeded, MissingKey, KeyNotConfirmed, Damaged, Unsupported, NotChecked, Error }

public sealed record KeyCoverageItem(string Path, string Format, KeyCoverageStatus Status, string Details,
    string? ContentId = null, string? KeySource = null, string? Fingerprint = null, bool PendingEdit = false)
{
    public bool NeedsAttention => Status is not (KeyCoverageStatus.Verified or KeyCoverageStatus.Opens or KeyCoverageStatus.NoKeyNeeded);
    public string StatusText => Status switch
    {
        KeyCoverageStatus.Opens => "Decrypts", KeyCoverageStatus.NoKeyNeeded => "No key needed",
        KeyCoverageStatus.MissingKey => "Needs key", KeyCoverageStatus.KeyNotConfirmed => "Key not confirmed",
        KeyCoverageStatus.NotChecked => "Not checked", _ => Status.ToString(),
    };
    public string DisplayPath => Path + (PendingEdit ? " [pending edit]" : "");
    public string Description => Details + (ContentId is null ? "" : $"\nContent ID: {ContentId}") +
        (KeySource is null ? "" : $"\nKey source: {KeySource}") + (Fingerprint is null ? "" : $"\nKey fingerprint: {Fingerprint}");
}

public sealed class PackageKeyCoverageReport
{
    public required string PackagePath { get; init; }
    public DateTimeOffset CheckedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyList<KeyCoverageItem> Items { get; init; }
    public int ScannedFiles { get; init; }
    public int OrdinaryFiles { get; init; }
    public int UsableCount => Items.Count(i => !i.NeedsAttention);
    public int AttentionCount => Items.Count(i => i.NeedsAttention);
    public string Summary => $"{UsableCount} usable · {AttentionCount} need attention · {ScannedFiles} files scanned";
    public string Note => $"Includes current pending edits. {OrdinaryFiles} ordinary {(OrdinaryFiles == 1 ? "file has" : "files have")} no recognized protection. " +
        "Verified means EDAT content integrity passed; Decrypts means decryption succeeded. Archives, nested packages, and disc containers are not scanned recursively. No plaintext or keys were saved.";
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    public string ToText() => $"Package key coverage\n{PackagePath}\nChecked: {CheckedUtc:u}\n{Summary}\n{Note}\n\n" +
        string.Join("\n\n", Items.Select(i => $"[{i.StatusText}] {i.DisplayPath} ({i.Format})\n{i.Description}"));
}

public static class PackageKeyCoverageService
{
    public static PackageKeyCoverageReport Scan(PackageOperationService package, ContentDecryptOptions? options = null,
        CancellationToken token = default, IProgress<ContentDecryptProgress>? progress = null)
    {
        options ??= new();
        if (options.MaxBufferedFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxBufferedFileBytes));
        if (!package.Info.IsDecrypted) throw new PkgKeyException("Open the package with a usable package key before scanning its contents.");
        token.ThrowIfCancellationRequested();
        var files = package.Info.Entries.Where(e => e.IsFile).ToArray();
        var items = new List<KeyCoverageItem>();
        int ordinary = 0;
        for (int index = 0; index < files.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = files[index];
            progress?.Report(new($"Checking {entry.Name}", index * 100d / files.Length));
            KeyCoverageItem? item;
            try { item = Inspect(package, entry, options, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is PkgFormatException or EndOfStreamException or InvalidDataException or OverflowException)
            { item = new(entry.Name, "Unknown", KeyCoverageStatus.Damaged, "Could not read the file structure. " + ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or PkgKeyException or CryptographicException or NotSupportedException)
            { item = new(entry.Name, "Unknown", KeyCoverageStatus.Error, "Could not complete this check. " + ex.Message); }
            if (item is null) ordinary++;
            else items.Add(item with { PendingEdit = package.IsReplaced(entry) });
        }
        token.ThrowIfCancellationRequested();
        progress?.Report(new("Key coverage complete", 100));
        return new() { PackagePath = package.FilePath, Items = items, ScannedFiles = files.Length, OrdinaryFiles = ordinary };
    }

    private static KeyCoverageItem? Inspect(PackageOperationService package, PkgEntry entry, ContentDecryptOptions options, CancellationToken token)
    {
        byte[] prefix = package.ReadEntryPrefix(entry, 8);
        bool edat = EdatFile.IsEdat(prefix), self = SelfReader.IsSelf(prefix), psp = PspEdatFile.IsPspEncrypted(prefix);
        string format = edat ? "EDAT / SDAT" : self ? "SELF / EBOOT" : psp ? "PSP EDAT / PGD" : "Unknown";
        KeyCoverageItem Result(KeyCoverageStatus status, string detail) => new(entry.Name, format, status, detail);
        if (prefix.AsSpan().StartsWith("\u007fELF"u8))
            return new(entry.Name, "ELF", KeyCoverageStatus.NoKeyNeeded, "Already plaintext ELF; no decryption key is required. Executable integrity was not checked.");
        if (!edat && !self && !psp)
        {
            if (prefix.AsSpan().StartsWith("PS2\0"u8) || prefix.AsSpan().StartsWith("\0PBP"u8))
                return new(entry.Name, "Disc container", KeyCoverageStatus.Unsupported, "Use the PS2 Classics or PSP/PS1 exporter to inspect and decrypt this container.");
            return ProtectedName(entry.Name) ? Result(KeyCoverageStatus.Unsupported,
                "Protected-content filename without a supported header. It may be a different format or damaged; inspect the file.") : null;
        }
        if (package.Info.Header.IsPspPsVita && package.Info.Header.PspKeyType is >= 2 and <= 4)
            return Result(KeyCoverageStatus.Unsupported, "Vita inner protection requires compatible Vita tooling and license material.");
        if (package.GetEntrySize(entry) > (ulong)options.MaxBufferedFileBytes)
            return Result(KeyCoverageStatus.NotChecked, $"Exceeds this scan's {options.MaxBufferedFileBytes / (1024 * 1024)} MiB per-file limit. Extract and check separately; key availability is not confirmed.");
        byte[] data = package.ReadEntryBytes(entry, token);
        using var input = new MemoryStream(data, writable: false);
        if (edat)
        {
            format = data.Length >= 0x84 && (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x80)) & 0x01000000) != 0 ? "SDAT" : "EDAT";
            var check = EdatKeyValidationService.Check(input, Path.GetFileName(entry.Name), new(), options.RapDirectory, options.KlicenseeDatabasePath, token);
            var status = check.Status switch
            {
                EdatKeyCheckStatus.Verified => KeyCoverageStatus.Verified,
                EdatKeyCheckStatus.MissingKey => KeyCoverageStatus.MissingKey,
                EdatKeyCheckStatus.HeaderMismatch => KeyCoverageStatus.KeyNotConfirmed,
                EdatKeyCheckStatus.InvalidKey => KeyCoverageStatus.KeyNotConfirmed,
                EdatKeyCheckStatus.DamagedContent or EdatKeyCheckStatus.InvalidFile => KeyCoverageStatus.Damaged,
                EdatKeyCheckStatus.Unverifiable => KeyCoverageStatus.NotChecked,
                _ => KeyCoverageStatus.Error,
            };
            return new(entry.Name, format, status, check.Message, check.ContentId, check.Source, check.Fingerprint);
        }
        if (self)
        {
            var info = SelfReader.ParseInfo(input);
            bool debug = (info.KeyRevision & 0x8000) != 0;
            if (info.Elf is not { Is64Bit: true, IsBigEndian: true } ||
                (!debug && SelfKeyset.Find(info.RawProgramType, info.KeyRevision) is null))
                return Result(KeyCoverageStatus.Unsupported, $"Unsupported SELF format or unavailable public keyset for revision 0x{info.KeyRevision:X4}.");
            ResolvedEdatKey? key = null;
            if (!debug && info.Npdrm is { } npd)
            {
                try { key = EdatKeyValidationService.Resolve(npd.ContentId, Path.GetFileName(entry.Name), false,
                    (int)npd.RawLicenseType, new(), options.RapDirectory, options.KlicenseeDatabasePath); }
                catch (PkgKeyException ex) { return new(entry.Name, format, KeyCoverageStatus.MissingKey, ex.Message, npd.ContentId); }
                catch (ArgumentException ex) { return new(entry.Name, format, KeyCoverageStatus.KeyNotConfirmed, ex.Message, npd.ContentId); }
            }
            try
            {
                byte[] elf = SelfDecryptor.Decrypt(data, key?.Key).Elf;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!elf.AsSpan().StartsWith("\u007fELF"u8)) throw new PkgFormatException("Decryption did not produce an ELF.");
                    return new(entry.Name, format, KeyCoverageStatus.Opens,
                        "Decryption produced an ELF. This confirms the decrypt path, not Sony signatures or complete executable integrity.",
                        info.Npdrm?.ContentId, debug ? "Debug SELF (no external key)" : key?.Source ?? $"Built-in SELF keyset 0x{info.KeyRevision:X4}", key?.Fingerprint);
                }
                finally { CryptographicOperations.ZeroMemory(elf); }
            }
            catch (PkgKeyException ex)
            { return new(entry.Name, format, KeyCoverageStatus.KeyNotConfirmed, "The key was not confirmed; it may be incorrect or the executable damaged. " + ex.Message, info.Npdrm?.ContentId, key?.Source, key?.Fingerprint); }
        }
        // PSP fixed-key content has a separate format and does not use PS3 RAP/klicensee mappings.
        int offset = 0;
        string? contentId = null;
        if (PspEdatFile.IsPspEdat(prefix))
        {
            var info = PspEdatFile.ParseHeader(data); offset = info.HeaderSize; contentId = info.ContentId;
            if (!info.DrmFreeOrLocal) return Result(KeyCoverageStatus.Unsupported, "Fuse-bound PSP EDAT needs device-specific material and is unsupported.");
        }
        if (offset < 0 || offset + 0x90L > data.Length) throw new PkgFormatException("Truncated PSP PGD header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8)) != 1)
            return Result(KeyCoverageStatus.Unsupported, "Fuse-bound PSP PGD needs device-specific material and is unsupported.");
        PspEdatFile.Decrypt(input, Stream.Null);
        token.ThrowIfCancellationRequested();
        return new(entry.Name, format, KeyCoverageStatus.Opens, "PSP fixed-key decryption succeeded; no external RAP is required.", contentId, "Built-in PSP fixed keys");
    }

    private static bool ProtectedName(string name) =>
        new[] { ".edat", ".sdat", ".self", ".sprx", ".pgd" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ||
        Path.GetFileName(name).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(name).Equals("DOCUMENT.DAT", StringComparison.OrdinalIgnoreCase);
}
