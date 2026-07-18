using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public sealed record FirmwareAnalysisProgress(int Completed, int Total, string Path);

public enum FirmwareAnalysisState
{
    Patchable,
    NoPatchMarker,
    MissingRap,
    DecryptionUnavailable,
    Invalid,
}

public sealed record FirmwareAnalysisItem(
    string Path,
    string Kind,
    string? ContentId,
    ushort? KeyRevision,
    string? HeaderFirmware,
    string? SdkFirmware,
    int RequiredVersion,
    bool CanPatch,
    string Status,
    FirmwareAnalysisState State);

public sealed class FirmwareAnalysisReport
{
    public required string Source { get; init; }
    public IReadOnlyList<FirmwareAnalysisItem> Items { get; init; } = Array.Empty<FirmwareAnalysisItem>();
    public bool IsApplicable { get; init; } = true;
    public string? Guidance { get; init; }
    public int HighestRequiredVersion => Items.Count == 0 ? 0 : Items.Max(item => item.RequiredVersion);
    public string? HighestRequiredFirmware => FormatVersion(HighestRequiredVersion);
    public int PatchableCount => Items.Count(item => item.CanPatch);
    public int MissingRapCount => Items.Count(item => item.State == FirmwareAnalysisState.MissingRap);
    public int FullyAnalyzedCount => Items.Count(item => item.State is FirmwareAnalysisState.Patchable or
        FirmwareAnalysisState.NoPatchMarker);
    public int UnsupportedCount => Items.Count(item => !item.CanPatch);

    public static string? FormatVersion(int version) => version <= 0 ? null : $"{version / 100}.{version % 100:D2}";
}

public sealed class FirmwareAnalysisOptions
{
    public string? RapDirectory { get; init; }
    public int MaximumExecutableBytes { get; init; } = 512 * 1024 * 1024;
}

/// <summary>Scans standalone files, extracted folders, or PS3 packages for SELF/SPRX firmware requirements.</summary>
public static class FirmwareAnalyzer
{
    private const uint ElfMagic = 0x7F454C46;

    public static FirmwareAnalysisReport Analyze(string path, IKeyProvider? packageKeys = null,
        FirmwareAnalysisOptions? options = null, CancellationToken cancellationToken = default,
        IProgress<FirmwareAnalysisProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new FirmwareAnalysisOptions();
        if (options.MaximumExecutableBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumExecutableBytes),
                "The maximum executable size must be positive.");
        packageKeys ??= new FileKeyProvider();

        string source = Path.GetFullPath(path);
        AnalysisSourceResult result;
        if (File.Exists(source) && Path.GetExtension(source).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            result = AnalyzePackage(source, packageKeys, options, cancellationToken, progress);
        else if (File.Exists(source))
            result = new AnalysisSourceResult(
                new[] { AnalyzeFile(source, Path.GetFileName(source), options, cancellationToken) }, true, null);
        else if (Directory.Exists(source))
        {
            IReadOnlyList<FirmwareAnalysisItem> items = AnalyzeDirectory(source, options, cancellationToken, progress);
            result = new AnalysisSourceResult(items, true, items.Count == 0
                ? "No EBOOT.BIN, .self, or .sprx files were found in this folder."
                : null);
        }
        else
            throw new FileNotFoundException("Firmware analysis source was not found.", source);

        return new FirmwareAnalysisReport
        {
            Source = source,
            Items = result.Items,
            IsApplicable = result.IsApplicable,
            Guidance = result.Guidance,
        };
    }

    public static string ToJson(FirmwareAnalysisReport report) => JsonSerializer.Serialize(report,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });

    public static string ToText(FirmwareAnalysisReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("FIRMWARE ANALYSIS");
        text.AppendLine($"Source: {report.Source}");
        text.AppendLine($"Highest required firmware: {report.HighestRequiredFirmware ?? "unknown"}");
        text.AppendLine($"Executables: {report.Items.Count}; safely patchable: {report.PatchableCount}");
        if (!string.IsNullOrWhiteSpace(report.Guidance))
            text.AppendLine($"Guidance: {report.Guidance}");
        text.AppendLine();
        foreach (FirmwareAnalysisItem item in report.Items)
        {
            text.AppendLine(item.Path);
            text.AppendLine($"  kind/header key : {item.Kind} / {(item.KeyRevision is ushort revision ? $"0x{revision:X4}" : "unknown")}");
            text.AppendLine($"  firmware        : header {item.HeaderFirmware ?? "unknown"}; SDK {item.SdkFirmware ?? "unavailable"}");
            text.AppendLine($"  patch           : {(item.CanPatch ? "supported" : "not offered")} — {item.Status}");
        }
        return text.ToString();
    }

    private static IReadOnlyList<FirmwareAnalysisItem> AnalyzeDirectory(string root,
        FirmwareAnalysisOptions options, CancellationToken cancellationToken,
        IProgress<FirmwareAnalysisProgress>? progress)
    {
        FileInfo[] files = SafeFileTree.Enumerate(new DirectoryInfo(root)).OfType<FileInfo>()
            .Where(file => IsExecutableName(file.Name))
            .ToArray();
        var items = new List<FirmwareAnalysisItem>(files.Length);
        for (int index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file = files[index];
            string display = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            items.Add(AnalyzeFile(file.FullName, display, options, cancellationToken));
            progress?.Report(new FirmwareAnalysisProgress(index + 1, files.Length, display));
        }
        return items;
    }

    private static AnalysisSourceResult AnalyzePackage(string path, IKeyProvider packageKeys,
        FirmwareAnalysisOptions options, CancellationToken cancellationToken,
        IProgress<FirmwareAnalysisProgress>? progress)
    {
        using var package = File.OpenRead(path);
        int headerLength = (int)Math.Min(package.Length, PkgHeader.ExtendedLength);
        var headerBytes = new byte[headerLength];
        package.ReadExactly(headerBytes);
        PkgHeader header = PkgHeader.Parse(headerBytes);
        if (!header.IsPs3)
        {
            string guidance = header.PspKeyType == 1
                ? "This is a PSP package. PSP packages contain EBOOT.PBP and PSP executables, not PS3 SELF/SPRX files, so PS3 firmware analysis does not apply. Use File → Export PSP package… instead."
                : "This is a PSVita package. Vita executables use a different firmware and module format, so PS3 SELF/SPRX firmware analysis does not apply. Use File → Export PSVita package… instead.";
            return new AnalysisSourceResult(Array.Empty<FirmwareAnalysisItem>(), false, guidance);
        }
        package.Position = 0;
        PkgInfo info = PkgReader.Read(package, packageKeys);
        if (!info.IsDecrypted)
            throw new PkgKeyException(info.DecryptionNote ?? "The package contents could not be decrypted.");
        PkgEntry[] entries = info.Entries.Where(entry => entry.IsFile && IsExecutableName(entry.Name)).ToArray();
        var items = new List<FirmwareAnalysisItem>(entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PkgEntry entry = entries[index];
            if (entry.FileSize > (ulong)options.MaximumExecutableBytes || entry.FileSize > int.MaxValue)
            {
                items.Add(Error(entry.Name, $"Executable is too large to inspect ({entry.FileSize:n0} bytes)."));
            }
            else
            {
                using var content = new MemoryStream((int)entry.FileSize);
                PkgReader.ExtractEntry(package, info.Header, entry, content, packageKeys, cancellationToken);
                items.Add(AnalyzeBytes(content.ToArray(), entry.Name, options));
            }
            progress?.Report(new FirmwareAnalysisProgress(index + 1, entries.Length, entry.Name));
        }
        return new AnalysisSourceResult(items, true, items.Count == 0
            ? "This PS3 package contains no EBOOT.BIN, .self, or .sprx executables to analyze."
            : null);
    }

    private static FirmwareAnalysisItem AnalyzeFile(string path, string display,
        FirmwareAnalysisOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var info = new FileInfo(path);
            if (info.Length > options.MaximumExecutableBytes)
                return Error(display, $"Executable is too large to inspect ({info.Length:n0} bytes).");
            return AnalyzeBytes(File.ReadAllBytes(path), display, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PkgFormatException or PkgKeyException)
        {
            return Error(display, ex.Message);
        }
    }

    private static FirmwareAnalysisItem AnalyzeBytes(byte[] data, string path, FirmwareAnalysisOptions options)
    {
        if (data.Length < 4)
            return Error(path, "The candidate is too small to be a SELF or ELF executable.");
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (magic == ElfMagic)
        {
            SdkVersion? plainSdk = EbootPatcher.FindSdkVersion(data);
            return plainSdk is null
                ? new FirmwareAnalysisItem(path, "Plain ELF", null, null, null, null, 0, false,
                    "Plain ELF has no sys_process_param SDK marker to patch.", FirmwareAnalysisState.NoPatchMarker)
                : new FirmwareAnalysisItem(path, "Plain ELF", null, null, null, plainSdk.Display,
                    plainSdk.ComparableVersion, true,
                    "Plain ELF SDK marker is patchable; package conversion will rebuild it as a CFW-only fake-signed SELF.",
                    FirmwareAnalysisState.Patchable);
        }
        if (magic != SelfReader.SceMagic)
            return Error(path, "The candidate is not a SELF/SCE executable.");

        SelfInfo info;
        try { info = SelfReader.ParseInfo(new MemoryStream(data, writable: false)); }
        catch (Exception ex) when (ex is PkgFormatException or IOException) { return Error(path, ex.Message); }

        int headerVersion = ToComparable(info.FirmwareVersion);
        string? headerDisplay = info.FirmwareVersionText;
        try
        {
            byte[]? klicensee = null;
            if (!info.IsLikelyFakeSigned && info.Npdrm is { LicenseType: not null and not NpdrmLicenseType.Free } licensed)
            {
                byte[]? rap = RapStore.Find(licensed.ContentId, options.RapDirectory);
                if (rap is null)
                    return Item(info, path, headerDisplay, null, headerVersion, false,
                        $"Header analyzed; RAP missing for {licensed.ContentId}, so safe patching cannot be verified.",
                        FirmwareAnalysisState.MissingRap);
                klicensee = NpdKeys.RapToKlicensee(rap);
            }

            SelfDecryptResult decrypted = SelfDecryptor.Decrypt(data, klicensee);
            SdkVersion? sdk = EbootPatcher.FindSdkVersion(decrypted.Elf);
            if (sdk is null)
                return Item(info, path, headerDisplay, null, headerVersion, false,
                    "Decrypted successfully, but no sys_process_param SDK marker is available to patch.",
                    FirmwareAnalysisState.NoPatchMarker);
            int required = Math.Max(headerVersion, sdk.ComparableVersion);
            return Item(info, path, headerDisplay, sdk.Display, required, true,
                "Safe firmware lowering is supported when rebuilding as a CFW-only fake-signed SELF.",
                FirmwareAnalysisState.Patchable);
        }
        catch (Exception ex) when (ex is PkgFormatException or PkgKeyException or CryptographicException or
                                   IOException or ArgumentException)
        {
            return Item(info, path, headerDisplay, null, headerVersion, false,
                $"Header analyzed; executable could not be decrypted for patch verification: {ex.Message}",
                FirmwareAnalysisState.DecryptionUnavailable);
        }
    }

    private static FirmwareAnalysisItem Item(SelfInfo info, string path, string? headerFirmware,
        string? sdkFirmware, int requiredVersion, bool canPatch, string status, FirmwareAnalysisState state) => new(
        path, info.ProgramTypeText, info.Npdrm?.ContentId, info.KeyRevision, headerFirmware,
        sdkFirmware, requiredVersion, canPatch, status, state);

    private static FirmwareAnalysisItem Error(string path, string status) =>
        new(path, "Unknown", null, null, null, null, 0, false, status, FirmwareAnalysisState.Invalid);

    private static bool IsExecutableName(string path)
    {
        string name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
        return name.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".self", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".sprx", StringComparison.OrdinalIgnoreCase);
    }

    private static int ToComparable(ulong firmwareVersion)
    {
        if (firmwareVersion == 0) return 0;
        ulong major = firmwareVersion / 10000;
        ulong minor = firmwareVersion % 10000 / 100;
        return major > 99 || minor > 99 ? 0 : checked((int)(major * 100 + minor));
    }

    private sealed record AnalysisSourceResult(
        IReadOnlyList<FirmwareAnalysisItem> Items,
        bool IsApplicable,
        string? Guidance);
}
