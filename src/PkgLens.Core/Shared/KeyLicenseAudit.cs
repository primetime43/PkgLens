using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public enum KeyLicenseAuditKind
{
    Package,
    Self,
    Edat,
    Sdat,
    PspEdat,
    PspPgd,
    PlainElf,
    Unrecognized,
}

public enum KeyLicenseAuditStatus
{
    NotRequired,
    Available,
    Missing,
    Supported,
    Unsupported,
    Unknown,
    Error,
}

public sealed record KeyLicenseAuditItem(
    string Source,
    KeyLicenseAuditKind Kind,
    string? RequiredKey,
    KeyLicenseAuditStatus KeyStatus,
    ushort? SelfRevision,
    string? ContentId,
    string? LicenseType,
    KeyLicenseAuditStatus RapStatus,
    string? RapPath,
    KeyLicenseAuditStatus EncryptionStatus,
    string? Details);

public sealed record KeyLicenseAuditProgress(int Completed, int Total, string Source);

public sealed class KeyLicenseAuditReport
{
    public required string Source { get; init; }
    public required string RapDirectory { get; init; }
    public IReadOnlyList<KeyLicenseAuditItem> Items { get; init; } = Array.Empty<KeyLicenseAuditItem>();

    public int PackageCount => Items.Count(item => item.Kind == KeyLicenseAuditKind.Package);
    public int SelfCount => Items.Count(item => item.Kind == KeyLicenseAuditKind.Self);
    public int DataFileCount => Items.Count(item => item.Kind is KeyLicenseAuditKind.Edat or
        KeyLicenseAuditKind.Sdat or KeyLicenseAuditKind.PspEdat or KeyLicenseAuditKind.PspPgd);
    public int MissingRapCount => Items.Count(item => item.RapStatus == KeyLicenseAuditStatus.Missing);
    public int UnsupportedCount => Items.Count(item => item.EncryptionStatus == KeyLicenseAuditStatus.Unsupported ||
        item.KeyStatus == KeyLicenseAuditStatus.Unsupported);
    public int ErrorCount => Items.Count(item => item.KeyStatus == KeyLicenseAuditStatus.Error ||
        item.EncryptionStatus == KeyLicenseAuditStatus.Error);
}

public sealed class KeyLicenseAuditOptions
{
    public string? RapDirectory { get; init; }
    public int PackageEntryPrefixBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>Read-only audit of package keys, SELF revisions, content licenses, and RAP availability.</summary>
public static class KeyLicenseAudit
{
    private const int MagicLength = 8;

    public static KeyLicenseAuditReport Inspect(string path, IKeyProvider? keys = null,
        KeyLicenseAuditOptions? options = null, CancellationToken cancellationToken = default,
        IProgress<KeyLicenseAuditProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new KeyLicenseAuditOptions();
        if (options.PackageEntryPrefixBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.PackageEntryPrefixBytes),
                options.PackageEntryPrefixBytes, "The package entry prefix size must be positive.");

        keys ??= new FileKeyProvider();
        string fullPath = Path.GetFullPath(path);
        var items = new List<KeyLicenseAuditItem>();
        IReadOnlyList<string> files;

        if (File.Exists(fullPath))
        {
            files = new[] { fullPath };
        }
        else if (Directory.Exists(fullPath))
        {
            var root = new DirectoryInfo(fullPath);
            files = SafeFileTree.Enumerate(root).OfType<FileInfo>()
                .Where(IsCandidate)
                .Select(file => file.FullName)
                .ToArray();
        }
        else
        {
            throw new FileNotFoundException("Audit source was not found.", fullPath);
        }

        for (int index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = files[index];
            string display = Directory.Exists(fullPath)
                ? Path.GetRelativePath(fullPath, file).Replace('\\', '/')
                : file;
            InspectFile(file, display, keys, options, items, cancellationToken);
            progress?.Report(new KeyLicenseAuditProgress(index + 1, files.Count, display));
        }

        return new KeyLicenseAuditReport
        {
            Source = fullPath,
            RapDirectory = RapStore.DirectoryPath(options.RapDirectory),
            Items = items,
        };
    }

    public static string ToJson(KeyLicenseAuditReport report) => JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    });

    public static string ToText(KeyLicenseAuditReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("KEY / LICENSE AUDIT");
        text.AppendLine($"Source: {report.Source}");
        text.AppendLine($"RAP library: {report.RapDirectory}");
        text.AppendLine($"Summary: {report.PackageCount} package(s), {report.SelfCount} SELF(s), " +
                        $"{report.DataFileCount} encrypted data file(s), {report.MissingRapCount} missing RAP(s), " +
                        $"{report.UnsupportedCount} unsupported, {report.ErrorCount} error(s)");

        foreach (KeyLicenseAuditItem item in report.Items)
        {
            text.AppendLine();
            text.AppendLine($"[{item.Kind}] {item.Source}");
            if (!string.IsNullOrWhiteSpace(item.RequiredKey))
                text.AppendLine($"  Required key: {item.RequiredKey} ({item.KeyStatus})");
            if (item.SelfRevision is ushort revision)
                text.AppendLine($"  SELF revision: 0x{revision:X4}");
            if (!string.IsNullOrWhiteSpace(item.ContentId))
                text.AppendLine($"  Content ID: {item.ContentId}");
            if (!string.IsNullOrWhiteSpace(item.LicenseType))
                text.AppendLine($"  License: {item.LicenseType}");
            text.AppendLine($"  RAP: {item.RapStatus}" +
                            (string.IsNullOrWhiteSpace(item.RapPath) ? string.Empty : $" — {item.RapPath}"));
            text.AppendLine($"  Encryption: {item.EncryptionStatus}");
            if (!string.IsNullOrWhiteSpace(item.Details))
                text.AppendLine($"  Details: {item.Details}");
        }

        if (report.Items.Count == 0)
            text.AppendLine("No package, SELF, EDAT/SDAT, PSP EDAT/PGD, or ELF files were found.");
        return text.ToString();
    }

    private static bool IsCandidate(FileInfo file)
    {
        string name = file.Name;
        string extension = file.Extension;
        return extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".self", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sprx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".edat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sdat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pgd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".elf", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("DOCUMENT.DAT", StringComparison.OrdinalIgnoreCase);
    }

    private static void InspectFile(string path, string display, IKeyProvider keys, KeyLicenseAuditOptions options,
        List<KeyLicenseAuditItem> items, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = File.OpenRead(path);
            byte[] magic = ReadPrefix(stream, MagicLength);
            if (Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase) || IsPkg(magic))
            {
                InspectPackage(stream, display, keys, options, items, cancellationToken);
                return;
            }
            InspectPayload(stream, display, options.RapDirectory, items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PkgFormatException or PkgKeyException)
        {
            items.Add(ErrorItem(display, ex.Message));
        }
    }

    private static void InspectPackage(Stream stream, string display, IKeyProvider keys, KeyLicenseAuditOptions options,
        List<KeyLicenseAuditItem> items, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        PkgInfo info = PkgReader.Read(stream, keys);
        (string requiredKey, bool schemeSupported) = PackageKeyDescription(info.Header, keys);
        items.Add(new KeyLicenseAuditItem(
            display,
            KeyLicenseAuditKind.Package,
            requiredKey,
            schemeSupported ? info.IsDecrypted ? KeyLicenseAuditStatus.Available : KeyLicenseAuditStatus.Missing
                : KeyLicenseAuditStatus.Unsupported,
            null,
            EmptyToNull(info.ContentId.Raw),
            info.Metadata.DrmType is uint drm ? DrmType.Name(drm) : null,
            KeyLicenseAuditStatus.NotRequired,
            null,
            schemeSupported ? KeyLicenseAuditStatus.Supported : KeyLicenseAuditStatus.Unsupported,
            info.IsDecrypted ? $"{info.Header.PlatformDisplay} {info.Header.Finalization}; package contents decrypted."
                : info.DecryptionNote));

        if (!info.IsDecrypted)
            return;

        foreach (PkgEntry entry in info.Entries.Where(entry => entry.IsFile && IsCandidateName(entry.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string innerDisplay = $"{display}::{entry.Name}";
            try
            {
                byte[] prefix = PkgReader.ExtractEntryPrefix(stream, info.Header, entry, keys,
                    options.PackageEntryPrefixBytes);
                using var payload = new MemoryStream(prefix, writable: false);
                InspectPayload(payload, innerDisplay, options.RapDirectory, items);
            }
            catch (Exception ex) when (ex is PkgFormatException or PkgKeyException or IOException)
            {
                items.Add(ErrorItem(innerDisplay, ex.Message));
            }
        }
    }

    private static void InspectPayload(Stream stream, string display, string? rapDirectory,
        List<KeyLicenseAuditItem> items)
    {
        byte[] magic = ReadPrefix(stream, MagicLength);
        stream.Position = 0;
        if (SelfReader.IsSelf(magic))
        {
            AddSelf(SelfReader.ParseInfo(stream), display, rapDirectory, items);
            return;
        }
        if (EdatFile.IsEdat(magic))
        {
            AddEdat(EdatFile.ParseHeader(stream), display, rapDirectory, items);
            return;
        }
        if (PspEdatFile.IsPspEdat(magic))
        {
            AddPspEdat(PspEdatFile.ParseHeader(ReadPrefix(stream, 0x40)), display, items);
            return;
        }
        if (PspEdatFile.IsRawPgd(magic))
        {
            items.Add(new KeyLicenseAuditItem(display, KeyLicenseAuditKind.PspPgd,
                "PSP PGD fixed/fuse key (selected during decryption)", KeyLicenseAuditStatus.Unknown,
                null, null, "PSP PGD", KeyLicenseAuditStatus.NotRequired, null,
                KeyLicenseAuditStatus.Unknown, "The header alone cannot distinguish fixed-key from fuse-bound PGD content."));
            return;
        }
        if (IsElf(magic))
        {
            items.Add(new KeyLicenseAuditItem(display, KeyLicenseAuditKind.PlainElf, null,
                KeyLicenseAuditStatus.NotRequired, null, null, "None", KeyLicenseAuditStatus.NotRequired,
                null, KeyLicenseAuditStatus.NotRequired, "Plaintext ELF; no SELF encryption or license."));
            return;
        }

        items.Add(new KeyLicenseAuditItem(display, KeyLicenseAuditKind.Unrecognized, null,
            KeyLicenseAuditStatus.Unknown, null, null, null, KeyLicenseAuditStatus.Unknown, null,
            KeyLicenseAuditStatus.Unsupported, "The file has a protected-content extension/name but no supported magic."));
    }

    private static void AddSelf(SelfInfo info, string display, string? rapDirectory,
        List<KeyLicenseAuditItem> items)
    {
        bool fakeSigned = info.IsLikelyFakeSigned;
        bool supported = fakeSigned || SelfKeyset.Find(info.RawProgramType, info.KeyRevision) is not null;
        string requiredKey = fakeSigned
            ? "None (fake-signed SELF)"
            : $"PS3 {info.ProgramTypeText} SELF keyset revision 0x{info.KeyRevision:X4}";
        (KeyLicenseAuditStatus rapStatus, string? rapPath, string? rapDetails) =
            ResolveRap(info.Npdrm?.ContentId, info.Npdrm is { LicenseType: NpdrmLicenseType.Local or NpdrmLicenseType.Network }, rapDirectory);

        items.Add(new KeyLicenseAuditItem(display, KeyLicenseAuditKind.Self, requiredKey,
            fakeSigned ? KeyLicenseAuditStatus.NotRequired : supported ? KeyLicenseAuditStatus.Available : KeyLicenseAuditStatus.Unsupported,
            info.KeyRevision, EmptyToNull(info.Npdrm?.ContentId), info.Npdrm?.LicenseText ?? "No NPDRM license",
            rapStatus, rapPath, supported ? KeyLicenseAuditStatus.Supported : KeyLicenseAuditStatus.Unsupported,
            rapDetails ?? (fakeSigned ? "CFW-style fSELF; retail metadata decryption is not required."
                : $"Program type: {info.ProgramTypeText}.")));
    }

    private static void AddEdat(NpdInfo info, string display, string? rapDirectory,
        List<KeyLicenseAuditItem> items)
    {
        bool supported = info.Version is >= 0 and <= 4;
        (KeyLicenseAuditStatus rapStatus, string? rapPath, string? rapDetails) =
            ResolveRap(info.ContentId, info.NeedsKlicensee, rapDirectory);
        string key = info.IsSdat ? "Built-in SDAT key" : info.IsFree ? "Built-in free NPDRM klicensee" : "RAP-derived klicensee";
        items.Add(new KeyLicenseAuditItem(display, info.IsSdat ? KeyLicenseAuditKind.Sdat : KeyLicenseAuditKind.Edat,
            key, info.NeedsKlicensee ? rapStatus : KeyLicenseAuditStatus.Available, null,
            EmptyToNull(info.ContentId), info.IsSdat ? $"SDAT / {info.LicenseText}" : info.LicenseText,
            rapStatus, rapPath, supported ? KeyLicenseAuditStatus.Supported : KeyLicenseAuditStatus.Unsupported,
            rapDetails ?? $"NPD version {info.Version}; flags 0x{info.Flags:X8}."));
    }

    private static void AddPspEdat(PspEdatInfo info, string display, List<KeyLicenseAuditItem> items)
    {
        bool supported = info.DrmFreeOrLocal;
        items.Add(new KeyLicenseAuditItem(display, KeyLicenseAuditKind.PspEdat,
            supported ? "Built-in PSP AMCTRL/PGD fixed key" : "Console fuse-bound PSP key",
            supported ? KeyLicenseAuditStatus.Available : KeyLicenseAuditStatus.Unsupported,
            null, EmptyToNull(info.ContentId), $"PSP DRM {info.DrmType}", KeyLicenseAuditStatus.NotRequired,
            null, supported ? KeyLicenseAuditStatus.Supported : KeyLicenseAuditStatus.Unsupported,
            supported ? "Fixed-key PSP EDAT; no RAP is used." : "Fuse-bound PSP EDAT encryption is not supported."));
    }

    private static (KeyLicenseAuditStatus Status, string? Path, string? Details) ResolveRap(
        string? contentId, bool required, string? rapDirectory)
    {
        if (!required)
            return (KeyLicenseAuditStatus.NotRequired, null, null);
        if (string.IsNullOrWhiteSpace(contentId))
            return (KeyLicenseAuditStatus.Missing, null, "A RAP is required, but the content ID is empty.");
        try
        {
            string path = RapStore.PathFor(contentId, rapDirectory);
            return RapStore.Find(contentId, rapDirectory) is not null
                ? (KeyLicenseAuditStatus.Available, path, null)
                : (KeyLicenseAuditStatus.Missing, path, "No valid 16-byte RAP is installed for this content ID.");
        }
        catch (ArgumentException ex)
        {
            return (KeyLicenseAuditStatus.Missing, null, $"The content ID cannot be resolved in the RAP library: {ex.Message}");
        }
    }

    private static (string Description, bool Supported) PackageKeyDescription(PkgHeader header, IKeyProvider keys)
    {
        if (header.Finalization == PkgFinalization.Debug)
            return ("Debug package SHA-1 keystream (derived from header)", true);
        if (header.Finalization != PkgFinalization.Retail)
            return ($"Unknown package finalization 0x{header.RawFinalization:X4}", false);
        if (header.IsPs3)
            return (keys is FileKeyProvider fileKeys && fileKeys.GetSelectedKeyName(header) is { } selected
                ? selected
                : "PS3 retail package AES key (standard, IDU/kiosk, or override; auto-detected)", true);
        if (header.IsPspPsVita)
            return header.PspKeyType switch
            {
                1 => ("Bundled PSP package AES key", true),
                2 or 3 or 4 => ($"Bundled PSVita package AES key revision {header.PspKeyType}", true),
                _ => ($"PSP/PSVita package AES key revision {header.PspKeyType}", false),
            };
        return ($"Unknown package platform 0x{header.RawPlatform:X4}", false);
    }

    private static bool IsCandidateName(string name)
    {
        string leaf = Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar));
        string extension = Path.GetExtension(leaf);
        return leaf.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
               leaf.Equals("DOCUMENT.DAT", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".self", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sprx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".edat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sdat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pgd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".elf", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadPrefix(Stream stream, int count)
    {
        stream.Position = 0;
        int length = (int)Math.Min(count, stream.Length);
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        stream.Position = 0;
        return bytes;
    }

    private static bool IsPkg(ReadOnlySpan<byte> magic) => magic.Length >= 4 &&
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(magic) == PkgHeader.Magic;
    private static bool IsElf(ReadOnlySpan<byte> magic) => magic.Length >= 4 &&
        magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F';
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static KeyLicenseAuditItem ErrorItem(string source, string details) => new(source,
        KeyLicenseAuditKind.Unrecognized, null, KeyLicenseAuditStatus.Error, null, null, null,
        KeyLicenseAuditStatus.Unknown, null, KeyLicenseAuditStatus.Error, details);
}
