using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public enum ContentDecryptStatus { Extracted, Decrypted, MissingKey, Unsupported, Failed }

public sealed record ContentDecryptItem(string Path, string Format, ContentDecryptStatus Status,
    string? ContentId, string Details, bool IncludedPendingEdit = false)
{
    [JsonIgnore]
    public string StatusText => Status == ContentDecryptStatus.MissingKey ? "Needs key" : Status.ToString();
}

public sealed class ContentDecryptReport
{
    public required string OutputDirectory { get; init; }
    public required IReadOnlyList<ContentDecryptItem> Items { get; init; }
    public int DecryptedCount => Items.Count(item => item.Status == ContentDecryptStatus.Decrypted);
    public int ExtractedCount => Items.Count(item => item.Status == ContentDecryptStatus.Extracted);
    public int AttentionCount => Items.Count - DecryptedCount - ExtractedCount;
    public string Summary => $"{DecryptedCount} decrypted, {ExtractedCount} extracted, {AttentionCount} need attention";
    public string Note { get; init; } = "Files retain their original names. Decrypted SELF/SPRX files contain ELF data. " +
        "Files that cannot be decrypted are kept unchanged. This is an inspection folder, not an installable game. " +
        "Archives and disc images are not unpacked recursively.";

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    });
}

public sealed class ContentDecryptOptions
{
    public string? RapDirectory { get; init; }
    public string? KlicenseeDatabasePath { get; init; }
    // Existing SELF and PSP decryptors buffer their input. Keep a bounded working set.
    public long MaxBufferedFileBytes { get; init; } = 128L * 1024 * 1024;
}

public sealed record ContentDecryptProgress(string Message, double Percent);

/// <summary>Exports the editing version of a package and decrypts supported inner files.</summary>
public static class PackageContentDecryptor
{
    public static ContentDecryptReport Export(Stream package, PkgInfo info, IKeyProvider keys,
        string outputDirectory, ContentDecryptOptions? options = null,
        IReadOnlyDictionary<PkgEntry, byte[]>? replacements = null,
        CancellationToken cancellationToken = default, IProgress<ContentDecryptProgress>? progress = null)
    {
        options ??= new ContentDecryptOptions();
        if (options.MaxBufferedFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxBufferedFileBytes));
        if (!info.IsDecrypted)
            throw new PkgKeyException(info.DecryptionNote ?? "A package key is required.");
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (Path.Exists(destination))
            throw new IOException("Choose a new output folder; existing files will not be overwritten.");
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Choose an output parent folder.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        SafeFileTree.ThrowIfLink(new DirectoryInfo(parent));

        // Validate before writing. Case-insensitive uniqueness also makes exports portable to Windows/macOS.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in info.Entries)
        {
            ValidateName(entry.Name);
            if (!names.Add(entry.Name.TrimEnd('/')))
                throw new PkgFormatException($"Duplicate output path: {entry.Name}");
        }
        string staging = Path.Combine(parent, $".pkglens-decrypt-{Guid.NewGuid():N}");
        string filesRoot = Path.Combine(staging, "files");
        string scratch = Path.Combine(staging, "decrypt.tmp");
        Directory.CreateDirectory(filesRoot);
        try
        {
            var items = new List<ContentDecryptItem>();
            var files = info.Entries.Where(entry => entry.IsFile).ToArray();
            foreach (var directory in info.Entries.Where(entry => entry.IsDirectory))
                Directory.CreateDirectory(Inside(filesRoot, directory.Name));
            for (int index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = files[index];
                string path = Inside(filesRoot, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                bool edited = replacements is not null && replacements.ContainsKey(entry);
                progress?.Report(new($"Extracting {entry.Name}", index * 100d / files.Length));
                // Extraction/storage errors abort the export rather than publishing incomplete input files.
                using (var output = File.Create(path))
                {
                    if (edited)
                    {
                        using var input = new MemoryStream(replacements![entry], writable: false);
                        Copy(input, output, cancellationToken);
                    }
                    else
                    {
                        var byteProgress = new InlineProgress<long>(bytes => progress?.Report(new(
                            $"Extracting {entry.Name}", (index + (entry.FileSize == 0 ? 0 : bytes * 0.45 / entry.FileSize)) * 100d / files.Length)));
                        PkgReader.ExtractEntry(package, info.Header, entry, output, keys, cancellationToken, byteProgress);
                    }
                }
                progress?.Report(new($"Checking protected content: {entry.Name}", (index + 0.5) * 100d / files.Length));
                var item = DecryptFile(path, entry.Name, scratch, info, options, cancellationToken);
                items.Add(item with { IncludedPendingEdit = edited });
                progress?.Report(new($"Processed {entry.Name}", (index + 1) * 100d / files.Length));
            }
            var report = new ContentDecryptReport { OutputDirectory = destination, Items = items };
            File.WriteAllText(Path.Combine(staging, "decryption-report.json"), report.ToJson());
            File.WriteAllText(Path.Combine(staging, "decryption-report.txt"),
                report.Summary + Environment.NewLine + report.Note + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, items.Select(item =>
                    $"[{item.Status}] {item.Path} ({item.Format}){(item.IncludedPendingEdit ? " [pending edit]" : "")}" +
                    $"{(item.ContentId is null ? "" : $" — {item.ContentId}")}: {item.Details}")));
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return report;
        }
        finally
        {
            // Only the unique directory created by this operation can be removed.
            if (Directory.Exists(staging) && Path.GetDirectoryName(Path.GetFullPath(staging)) == parent)
                Directory.Delete(staging, recursive: true);
        }
    }

    private static ContentDecryptItem DecryptFile(string path, string name, string scratch, PkgInfo package,
        ContentDecryptOptions options, CancellationToken token)
    {
        string format = "File";
        string? contentId = null;
        ContentDecryptItem Result(ContentDecryptStatus status, string detail) => new(name, format, status, contentId, detail);
        try
        {
            using (var input = File.OpenRead(path))
            {
                byte[] magic = new byte[(int)Math.Min(8, input.Length)];
                input.ReadExactly(magic);
                input.Position = 0;
                bool vita = package.Header.IsPspPsVita && package.Header.PspKeyType is >= 2 and <= 4;
                if (vita)
                    return Result(ContentDecryptStatus.Unsupported,
                        "Vita package layer extracted only; inner PFS/SELF content requires compatible Vita tooling and license material.");
                if (SelfReader.IsSelf(magic))
                {
                    format = "SELF → ELF";
                    var selfInfo = SelfReader.ParseInfo(input);
                    contentId = selfInfo.Npdrm?.ContentId;
                    bool debug = (selfInfo.KeyRevision & 0x8000) != 0;
                    if (selfInfo.Elf is not { Is64Bit: true, IsBigEndian: true } ||
                        (!debug && SelfKeyset.Find(selfInfo.RawProgramType, selfInfo.KeyRevision) is null))
                        return Result(ContentDecryptStatus.Unsupported, $"Unsupported SELF format/key revision 0x{selfInfo.KeyRevision:X4}; original retained.");
                    if (input.Length > options.MaxBufferedFileBytes)
                        return Result(ContentDecryptStatus.Unsupported, "Exceeds the buffered SELF size limit; original retained.");
                    byte[]? klic = null;
                    if (!debug && selfInfo.Npdrm is { } npd)
                    {
                        klic = ResolveKey(npd.ContentId, name, npd.RawLicenseType, options);
                        if (klic is null && npd.LicenseType != NpdrmLicenseType.Free)
                            return Result(ContentDecryptStatus.MissingKey, "Import the matching RAP or klicensee, then export again. Original retained.");
                    }
                    input.Position = 0;
                    using var buffered = new MemoryStream();
                    Copy(input, buffered, token);
                    byte[] elf = SelfDecryptor.Decrypt(buffered.ToArray(), klic).Elf;
                    token.ThrowIfCancellationRequested();
                    if (elf.Length < 4 || !elf.AsSpan(0, 4).SequenceEqual("\u007fELF"u8))
                        throw new PkgFormatException("Decryption did not produce an ELF.");
                    File.WriteAllBytes(scratch, elf);
                }
                else if (EdatFile.IsEdat(magic))
                {
                    var npd = EdatFile.ParseHeader(input);
                    format = npd.IsSdat ? "SDAT" : "EDAT";
                    contentId = npd.ContentId;
                    if (npd.Version is < 0 or > 4)
                        return Result(ContentDecryptStatus.Unsupported, $"Unsupported NPD version {npd.Version}; original retained.");
                    byte[]? klic = npd.NeedsKlicensee ? ResolveKey(npd.ContentId, name, (uint)npd.License, options) : null;
                    if (npd.NeedsKlicensee && klic is null)
                        return Result(ContentDecryptStatus.MissingKey, "Import the matching RAP or klicensee, then export again. Original retained.");
                    using var output = File.Create(scratch);
                    EdatFile.Decrypt(input, output, klic, token);
                    if (output.Length != npd.FileSize)
                        throw new PkgFormatException("Decrypted size does not match the NPD header.");
                }
                else if (PspEdatFile.IsPspEncrypted(magic))
                {
                    format = "PSP EDAT / PGD";
                    if (input.Length > options.MaxBufferedFileBytes)
                        return Result(ContentDecryptStatus.Unsupported, "Exceeds the buffered PSP size limit; original retained.");
                    int pgdOffset = 0;
                    if (PspEdatFile.IsPspEdat(magic))
                    {
                        byte[] header = new byte[0x40];
                        input.ReadExactly(header);
                        input.Position = 0;
                        var psp = PspEdatFile.ParseHeader(header);
                        pgdOffset = psp.HeaderSize;
                        contentId = psp.ContentId;
                        if (!psp.DrmFreeOrLocal)
                            return Result(ContentDecryptStatus.Unsupported, "Fuse-bound PSP content is unsupported; original retained.");
                    }
                    if (pgdOffset < 0 || pgdOffset + 0x90L > input.Length)
                        throw new PkgFormatException("Truncated PSP PGD header.");
                    input.Position = pgdOffset;
                    byte[] pgdHeader = new byte[12];
                    input.ReadExactly(pgdHeader);
                    if (PspEdatFile.IsRawPgd(pgdHeader) &&
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pgdHeader.AsSpan(8)) != 1)
                        return Result(ContentDecryptStatus.Unsupported, "Fuse-bound PSP PGD is unsupported; original retained.");
                    input.Position = 0;
                    using var output = File.Create(scratch);
                    PspEdatFile.Decrypt(input, output);
                }
                else if (magic.AsSpan().StartsWith("PS2\0"u8) || magic.AsSpan().StartsWith("\0PBP"u8))
                    return Result(ContentDecryptStatus.Unsupported, "Use the PS2 Classics or PSP/PS1 platform exporter to decrypt the inner disc image. Original retained.");
                else if (IsProtectedName(name) && !magic.AsSpan().StartsWith("\u007fELF"u8))
                    return Result(ContentDecryptStatus.Unsupported, "Protected-content filename with no supported header; extracted unchanged.");
                else
                    return Result(ContentDecryptStatus.Extracted, "Extracted unchanged; no supported inner encryption detected.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(scratch, path, overwrite: true);
            return Result(ContentDecryptStatus.Decrypted, "Decrypted successfully; original filename retained.");
        }
        catch (PkgKeyException ex) { return Result(ContentDecryptStatus.MissingKey, ex.Message + " Original retained."); }
        catch (Exception ex) when (ex is PkgFormatException or CryptographicException or InvalidDataException or
                                       EndOfStreamException or ArgumentException or OverflowException or NotSupportedException)
        {
            return Result(ContentDecryptStatus.Failed, ex.Message + " Original retained; check the key and file integrity.");
        }
        finally { if (File.Exists(scratch)) File.Delete(scratch); }
    }

    private static byte[]? ResolveKey(string contentId, string fileName, uint license, ContentDecryptOptions options)
    {
        if (string.IsNullOrWhiteSpace(contentId)) return null;
        var local = KlicenseeStore.Find(contentId, fileName, license, options.KlicenseeDatabasePath);
        var known = local ?? KnownKlicenseeStore.Find(contentId, fileName, license);
        if (known is not null) return known.Klicensee;
        byte[]? rap = RapStore.Find(contentId, options.RapDirectory);
        return rap is null ? null : NpdKeys.RapToKlicensee(rap);
    }

    private static bool IsProtectedName(string name) =>
        new[] { ".self", ".sprx", ".edat", ".sdat", ".pgd" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ||
        Path.GetFileName(name).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(name).Equals("DOCUMENT.DAT", StringComparison.OrdinalIgnoreCase);

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains('\\') ||
            name.TrimEnd('/').Split('/').Any(part => part is "" or "." or ".." ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') ||
                part.EndsWith('.') || part.EndsWith(' ') || IsDeviceName(part)))
            throw new PkgFormatException($"Unsafe package path: {name}");
    }

    private static bool IsDeviceName(string part)
    {
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string Inside(string root, string name)
    {
        string full = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PkgFormatException($"Package path escapes output: {name}");
        return full;
    }

    private static void Copy(Stream input, Stream output, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
    }
}
