using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public sealed record CfwFirmwareTarget(int Major, int Minor)
{
    public override string ToString() => $"{Major}.{Minor:D2}";
}

public sealed record CfwLicenseResolution(byte[]? Klicensee, string? Source);

public sealed class CfwConversionOptions
{
    public CfwFirmwareTarget? FirmwareTarget { get; set; }
    public Func<string, CfwLicenseResolution>? KlicenseeResolver { get; set; }
    public string? SourceName { get; set; }
    public string? OutputName { get; set; }
}

public sealed record CfwExecutableTransformation(
    string Path,
    long OriginalSize,
    long ConvertedSize,
    string Action,
    string? ContentId,
    string? LicenseSource,
    string? FirmwareChange);

public sealed record CfwConversionProgress(string Stage, double? Percent = null);

public sealed class CfwConversionReport
{
    public required string SourceName { get; init; }
    public required string OutputName { get; init; }
    public required string ContentId { get; init; }
    public required PkgFinalization SourceFinalization { get; init; }
    public required long SourceSize { get; init; }
    public required long OutputSize { get; init; }
    public required int PackageEntryCount { get; init; }
    public required CfwFirmwareTarget? FirmwareTarget { get; init; }
    public required IReadOnlyList<CfwExecutableTransformation> Executables { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine("PkgLens CFW conversion report");
        text.AppendLine("=============================");
        text.AppendLine($"Source package : {SourceName}");
        text.AppendLine($"Output package : {OutputName}");
        text.AppendLine($"Content ID     : {ContentId}");
        text.AppendLine($"Source format  : {SourceFinalization}");
        text.AppendLine($"Source size    : {SourceSize:n0} bytes");
        text.AppendLine($"Output size    : {OutputSize:n0} bytes");
        text.AppendLine($"Package entries: {PackageEntryCount:n0}");
        text.AppendLine($"Firmware patch : {(FirmwareTarget is null ? "disabled" : FirmwareTarget.ToString())}");
        text.AppendLine($"Executables    : {Executables.Count:n0}");

        foreach (CfwExecutableTransformation executable in Executables)
        {
            text.AppendLine();
            text.AppendLine(executable.Path);
            text.AppendLine($"  transformation : {executable.Action}");
            text.AppendLine($"  size           : {executable.OriginalSize:n0} → {executable.ConvertedSize:n0} bytes");
            if (!string.IsNullOrWhiteSpace(executable.ContentId))
                text.AppendLine($"  content ID     : {executable.ContentId}");
            if (!string.IsNullOrWhiteSpace(executable.LicenseSource))
                text.AppendLine($"  license        : {executable.LicenseSource}");
            if (!string.IsNullOrWhiteSpace(executable.FirmwareChange))
                text.AppendLine($"  firmware       : {executable.FirmwareChange}");
        }

        if (Warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Warnings");
            text.AppendLine("--------");
            foreach (string warning in Warnings)
                text.AppendLine($"- {warning}");
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }
}

public static class CfwPackageConverter
{
    private const uint SceMagic = 0x53434500;
    private const uint ElfMagic = 0x7F454C46;

    public static CfwConversionReport Convert(
        Stream source,
        Stream destination,
        IKeyProvider packageKeys,
        CfwConversionOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<CfwConversionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(packageKeys);
        options ??= new CfwConversionOptions();
        ValidateFirmwareTarget(options.FirmwareTarget);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanSeek)
            throw new PkgFormatException("A seekable package source is required for CFW conversion.");

        progress?.Report(new CfwConversionProgress("Reading package…", 0));
        PkgHeader rawHeader = ReadHeader(source);
        if (!rawHeader.IsPs3)
            throw new PkgFormatException("One-click CFW conversion supports PS3 packages only.");
        PkgInfo info = PkgReader.Read(source, packageKeys);
        if (!info.IsDecrypted)
            throw new PkgKeyException("The package contents could not be decrypted with the available package keys.");

        List<PkgEntry> executables = info.Entries
            .Where(entry => entry.IsFile && IsExecutable(entry.Name))
            .ToList();
        if (executables.Count == 0)
            throw new PkgFormatException("The package contains no EBOOT.BIN, SELF, or SPRX executables to convert.");

        var replacements = new Dictionary<PkgEntry, byte[]>();
        var transformations = new List<CfwExecutableTransformation>();
        var warnings = new List<string>
        {
            "The rebuilt package is unsigned and is intended only for CFW/HEN or compatible emulators, not stock OFW.",
        };

        for (int index = 0; index < executables.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PkgEntry entry = executables[index];
            progress?.Report(new CfwConversionProgress(
                $"Extracting and converting {entry.Name}…", index * 20d / executables.Count));
            byte[] original = ExtractExecutable(source, info.Header, entry, packageKeys, cancellationToken);
            (byte[] converted, CfwExecutableTransformation transformation, string? warning) =
                ConvertExecutable(entry.Name, original, options, cancellationToken);
            replacements[entry] = converted;
            transformations.Add(transformation);
            if (warning is not null) warnings.Add(warning);
        }

        progress?.Report(new CfwConversionProgress("Rebuilding streaming package…", 20));
        var repackProgress = progress is null ? null : new InlineProgress<PkgOperationProgress>(value =>
            progress.Report(new CfwConversionProgress(
                value.Item is null ? "Rebuilding streaming package…" : $"Repacking {value.Item}…",
                20 + value.Percent * 0.8)));
        PkgWriter.Repack(source, info, replacements, packageKeys, destination,
            cancellationToken, repackProgress);
        progress?.Report(new CfwConversionProgress("CFW conversion complete.", 100));

        return new CfwConversionReport
        {
            SourceName = options.SourceName ?? "(input stream)",
            OutputName = options.OutputName ?? "(output stream)",
            ContentId = info.Header.ContentId.Raw,
            SourceFinalization = info.Header.Finalization,
            SourceSize = source.Length,
            OutputSize = destination.CanSeek ? destination.Length : 0,
            PackageEntryCount = info.Entries.Count,
            FirmwareTarget = options.FirmwareTarget,
            Executables = transformations,
            Warnings = warnings,
        };
    }

    private static (byte[] Data, CfwExecutableTransformation Report, string? Warning) ConvertExecutable(
        string path,
        byte[] original,
        CfwConversionOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (original.Length < 4)
            throw new PkgFormatException($"'{path}' is too small to be an ELF or SELF.");

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(original);
        byte[] elf;
        bool npdrm;
        string? contentId;
        uint? npAppType;
        string action;
        string? licenseSource = null;
        SelfInfo? selfInfo = null;

        if (magic == ElfMagic)
        {
            elf = original;
            npdrm = false;
            contentId = null;
            npAppType = null;
            action = "plaintext ELF → fake-signed SELF";
        }
        else if (magic == SceMagic)
        {
            selfInfo = SelfReader.ParseInfo(new MemoryStream(original, writable: false));
            npdrm = selfInfo.IsNpdrm;
            contentId = selfInfo.Npdrm?.ContentId;
            npAppType = selfInfo.Npdrm?.AppType;

            if (selfInfo.IsLikelyFakeSigned && options.FirmwareTarget is null)
            {
                return (original, new CfwExecutableTransformation(
                    path, original.LongLength, original.LongLength, "already fake-signed; unchanged",
                    contentId, null, null), null);
            }

            byte[]? klicensee = null;
            if (!selfInfo.IsLikelyFakeSigned &&
                selfInfo.Npdrm is { LicenseType: not null and not NpdrmLicenseType.Free } npdrmInfo)
            {
                if (options.KlicenseeResolver is null)
                    throw MissingRap(path, npdrmInfo.ContentId);
                CfwLicenseResolution resolution = options.KlicenseeResolver(npdrmInfo.ContentId);
                klicensee = resolution.Klicensee;
                licenseSource = resolution.Source;
                if (klicensee is null)
                    throw MissingRap(path, npdrmInfo.ContentId);
                if (klicensee.Length != 16)
                    throw new PkgKeyException($"The resolved klicensee for '{npdrmInfo.ContentId}' is not 16 bytes.");
            }

            SelfDecryptResult decrypted = SelfDecryptor.Decrypt(original, klicensee);
            elf = decrypted.Elf;
            action = selfInfo.IsLikelyFakeSigned
                ? "fake-signed SELF rebuilt"
                : "encrypted SELF → decrypted ELF → fake-signed SELF";
        }
        else
        {
            throw new PkgFormatException($"'{path}' is neither an ELF nor a SELF.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? firmwareChange = null;
        string? warning = null;
        if (options.FirmwareTarget is { } target)
        {
            SdkVersion? previous = EbootPatcher.FindSdkVersion(elf);
            if (previous is null)
            {
                firmwareChange = $"target {target} requested; sys_process_param not found";
                warning = $"{path}: firmware was not changed because the ELF has no sys_process_param SDK marker.";
            }
            else if (previous.ComparableVersion <= target.Major * 100 + target.Minor)
            {
                firmwareChange = $"{previous.Display} already at or below {target}; unchanged";
            }
            else
            {
                EbootPatcher.SetFirmwareVersion(elf, target.Major, target.Minor);
                firmwareChange = $"{previous.Display} → {target}";
                action += " → firmware patched";
            }
        }

        var selfOptions = new SelfBuilder.FakeSelfOptions
        {
            Npdrm = npdrm,
            ContentId = contentId,
            NpLicenseType = npdrm ? (uint)NpdrmLicenseType.Free : null,
            NpAppType = npAppType,
        };
        byte[] converted = SelfBuilder.MakeFakeSelf(elf, selfOptions);
        return (converted, new CfwExecutableTransformation(
            path, original.LongLength, converted.LongLength, action, contentId,
            licenseSource, firmwareChange), warning);
    }

    private static bool IsExecutable(string path)
    {
        string name = path.Split('/', '\\').Last();
        return name.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".self", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".sprx", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ExtractExecutable(Stream source, PkgHeader header, PkgEntry entry,
        IKeyProvider packageKeys, CancellationToken cancellationToken)
    {
        if (entry.FileSize > int.MaxValue)
            throw new PkgFormatException($"'{entry.Name}' is too large to transform in memory ({entry.FileSize:n0} bytes).");
        using var extracted = new MemoryStream((int)entry.FileSize);
        PkgReader.ExtractEntry(source, header, entry, extracted, packageKeys, cancellationToken);
        return extracted.ToArray();
    }

    private static PkgHeader ReadHeader(Stream source)
    {
        source.Position = 0;
        int length = (int)Math.Min(source.Length, PkgHeader.ExtendedLength);
        var buffer = new byte[length];
        source.ReadExactly(buffer);
        return PkgHeader.Parse(buffer);
    }

    private static PkgKeyException MissingRap(string path, string contentId) => new(
        $"'{path}' is licensed for '{contentId}', but its RAP is not available in the RAP library.");

    private static void ValidateFirmwareTarget(CfwFirmwareTarget? target)
    {
        if (target is null) return;
        if (target.Major is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(target), "Firmware major version must be between 0 and 15.");
        if (target.Minor is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(target), "Firmware minor version must be between 0 and 99.");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
