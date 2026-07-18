using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Shared.Sfo;

namespace PkgLens.Core.Shared;

public enum PackageFileChange
{
    Unchanged,
    Added,
    Removed,
    Modified,
}

public sealed record PackageComparisonSide(
    string FilePath,
    string Platform,
    string? ContentId,
    string? TitleId,
    string? Title,
    string? Version,
    string? Category,
    PackageLibraryRole Role,
    int FileCount,
    long PackageSize);

public sealed record PackageFileDifference(
    string Path,
    PackageFileChange Change,
    ulong? BaseSize,
    ulong? TargetSize,
    string? BaseSha256,
    string? TargetSha256)
{
    [JsonIgnore]
    internal PkgEntry? TargetEntry { get; init; }
}

public sealed record PackageSfoDifference(
    string Key,
    string Change,
    string? BaseValue,
    string? TargetValue);

public sealed class PackageComparisonResult
{
    public required PackageComparisonSide Base { get; init; }
    public required PackageComparisonSide Target { get; init; }
    public required IReadOnlyList<PackageFileDifference> Files { get; init; }
    public required IReadOnlyList<PackageSfoDifference> SfoValues { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    [JsonIgnore]
    internal PkgInfo TargetInfo { get; init; } = null!;

    public int AddedCount => Files.Count(file => file.Change == PackageFileChange.Added);
    public int RemovedCount => Files.Count(file => file.Change == PackageFileChange.Removed);
    public int ModifiedCount => Files.Count(file => file.Change == PackageFileChange.Modified);
    public int UnchangedCount => Files.Count(file => file.Change == PackageFileChange.Unchanged);
    public int OverlayContentFileCount => Files.Count(file =>
        file.Change is PackageFileChange.Added or PackageFileChange.Modified &&
        !file.Path.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase));
    public long OverlayPayloadBytes => Files
        .Where(file => file.Change is PackageFileChange.Added or PackageFileChange.Modified)
        .Where(file => !file.Path.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase))
        .Sum(file => checked((long)(file.TargetSize ?? 0)));

    public bool CanBuildOverlay =>
        TargetInfo.Header.IsPs3 && TargetInfo.IsDecrypted && TargetInfo.Sfo is not null &&
        !string.IsNullOrWhiteSpace(Base.TitleId) &&
        string.Equals(Base.TitleId, Target.TitleId, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(TargetInfo.ContentId.Raw) &&
        Files.Any(file => file.Change is PackageFileChange.Added or PackageFileChange.Modified);
}

public sealed record PackageOverlayBuildResult(
    string OutputPath,
    int IncludedFileCount,
    long IncludedPayloadBytes,
    int OmittedRemovalCount,
    PkgFinalization Finalization,
    string ContentId);

public static class PackageComparison
{
    public static PackageComparisonResult Compare(string basePackagePath, string targetPackagePath,
        IKeyProvider keys, CancellationToken cancellationToken = default,
        IProgress<PkgOperationProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPackagePath);
        ArgumentNullException.ThrowIfNull(keys);
        string basePath = Path.GetFullPath(basePackagePath);
        string targetPath = Path.GetFullPath(targetPackagePath);
        if (PathsEqual(basePath, targetPath))
            throw new ArgumentException("Choose two different package files to compare.");

        using var baseStream = File.OpenRead(basePath);
        using var targetStream = File.OpenRead(targetPath);
        PkgInfo baseInfo = PkgReader.Read(baseStream, keys);
        PkgInfo targetInfo = PkgReader.Read(targetStream, keys);
        if (!baseInfo.IsDecrypted)
            throw new PkgKeyException(baseInfo.DecryptionNote ?? "The base package item table could not be decrypted.");
        if (!targetInfo.IsDecrypted)
            throw new PkgKeyException(targetInfo.DecryptionNote ?? "The target package item table could not be decrypted.");

        var baseFiles = baseInfo.Entries.Where(entry => entry.IsFile)
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var targetFiles = targetInfo.Entries.Where(entry => entry.IsFile)
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        string[] paths = baseFiles.Keys.Concat(targetFiles.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

        long totalBytes = checked(CheckedFileBytes(baseFiles.Values) + CheckedFileBytes(targetFiles.Values));
        long completed = 0;
        var baseHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string path, PkgEntry entry) in baseFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            baseHashes[path] = HashEntry(baseStream, baseInfo, entry, keys, cancellationToken, bytes =>
            {
                progress?.Report(new PkgOperationProgress(completed + bytes, totalBytes, $"Base: {path}"));
            });
            completed += checked((long)entry.FileSize);
        }

        var targetHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string path, PkgEntry entry) in targetFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long itemBase = completed;
            targetHashes[path] = HashEntry(targetStream, targetInfo, entry, keys, cancellationToken, bytes =>
            {
                progress?.Report(new PkgOperationProgress(itemBase + bytes, totalBytes, $"Target: {path}"));
            });
            completed += checked((long)entry.FileSize);
        }

        var files = new List<PackageFileDifference>(paths.Length);
        foreach (string path in paths)
        {
            baseFiles.TryGetValue(path, out PkgEntry? baseEntry);
            targetFiles.TryGetValue(path, out PkgEntry? targetEntry);
            PackageFileChange change = (baseEntry, targetEntry) switch
            {
                (null, not null) => PackageFileChange.Added,
                (not null, null) => PackageFileChange.Removed,
                (not null, not null) when baseHashes[path] == targetHashes[path] => PackageFileChange.Unchanged,
                _ => PackageFileChange.Modified,
            };
            files.Add(new PackageFileDifference(
                path,
                change,
                baseEntry?.FileSize,
                targetEntry?.FileSize,
                baseEntry is null ? null : baseHashes[path],
                targetEntry is null ? null : targetHashes[path])
            {
                TargetEntry = targetEntry,
            });
        }

        var warnings = BuildWarnings(baseInfo, targetInfo);
        return new PackageComparisonResult
        {
            Base = Describe(basePath, baseInfo),
            Target = Describe(targetPath, targetInfo),
            Files = files,
            SfoValues = CompareSfo(baseInfo.Sfo, targetInfo.Sfo),
            Warnings = warnings,
            TargetInfo = targetInfo,
        };
    }

    public static PackageOverlayBuildResult BuildOverlay(PackageComparisonResult comparison,
        string outputPath, IKeyProvider keys, CancellationToken cancellationToken = default,
        IProgress<PkgOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(keys);
        if (!comparison.CanBuildOverlay)
            throw new InvalidOperationException("This comparison cannot create an overlay package. A decrypted PS3 target with PARAM.SFO and at least one added or modified file is required.");

        PkgInfo targetInfo = comparison.TargetInfo;
        string targetPath = comparison.Target.FilePath;
        string destination = Path.GetFullPath(outputPath);
        if (PathsEqual(targetPath, destination) || PathsEqual(comparison.Base.FilePath, destination))
            throw new InvalidOperationException("The overlay output must not overwrite either compared package.");

        string stagingDirectory = Path.Combine(Path.GetTempPath(), $"pkglens-compare-{Guid.NewGuid():N}");
        string temporaryOutput = destination + $".{Guid.NewGuid():N}.pkglens-part";
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            var changed = comparison.Files
                .Where(file => file.Change is PackageFileChange.Added or PackageFileChange.Modified)
                .Where(file => !file.Path.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            long extractionTotal = changed.Sum(file => checked((long)(file.TargetSize ?? 0)));
            long extracted = 0;
            var staged = new List<(PackageFileDifference Difference, string StagedPath)>(changed.Length);
            using (var targetStream = File.OpenRead(targetPath))
            {
                for (int index = 0; index < changed.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PackageFileDifference difference = changed[index];
                    PkgEntry entry = difference.TargetEntry ?? throw new InvalidOperationException(
                        $"The target entry for {difference.Path} is unavailable.");
                    string stagedPath = Path.Combine(stagingDirectory, index.ToString("D8") + ".bin");
                    using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        long itemBase = extracted;
                        var itemProgress = new InlineProgress<long>(value => progress?.Report(new PkgOperationProgress(
                            itemBase + value, extractionTotal, $"Staging {difference.Path}")));
                        PkgReader.ExtractEntry(targetStream, targetInfo.Header, entry, output, keys,
                            cancellationToken, itemProgress);
                    }
                    extracted += checked((long)entry.FileSize);
                    staged.Add((difference, stagedPath));
                }
            }

            byte[] patchSfo = BuildPatchSfo(targetInfo.Sfo!);
            uint packageFlags = targetInfo.Metadata.Find(PkgMetadataId.PackageFlags)?.AsUInt32() ?? 0;
            var builder = new PkgBuilder
            {
                Finalization = targetInfo.Header.Finalization == PkgFinalization.Debug
                    ? PkgFinalization.Debug
                    : PkgFinalization.Retail,
                ContentId = targetInfo.ContentId.Raw,
                InstallDirectory = targetInfo.Metadata.InstallDirectory ?? targetInfo.Sfo?.TitleId ?? targetInfo.ContentId.TitleId,
                DrmType = (uint)PkgDrmType.Free,
                ContentType = (uint)PkgContentType.GameData,
                PackageFlags = packageFlags,
            };

            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach ((PackageFileDifference difference, _) in staged)
                AddParentDirectories(difference.Path, directories);
            foreach (string directory in directories.OrderBy(path => path.Count(character => character == '/'))
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
                builder.AddDirectory(directory);
            builder.AddFile("PARAM.SFO", patchSfo);
            foreach ((PackageFileDifference difference, string stagedPath) in staged)
            {
                long length = new FileInfo(stagedPath).Length;
                builder.AddFile(difference.Path, length,
                    () => new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                    difference.TargetEntry?.Kind ?? PkgEntryType.Regular);
            }

            using (var output = new FileStream(temporaryOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                builder.Build(output, keys, cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            PostOperationVerifier.VerifyPackage(temporaryOutput, keys);
            VerifyOverlayPayloads(temporaryOutput, staged, keys, cancellationToken, progress);
            File.Move(temporaryOutput, destination, true);
            return new PackageOverlayBuildResult(
                destination,
                staged.Count + 1,
                staged.Sum(item => new FileInfo(item.StagedPath).Length) + patchSfo.LongLength,
                comparison.RemovedCount,
                builder.Finalization,
                builder.ContentId);
        }
        catch
        {
            if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    public static string ToJson(PackageComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return JsonSerializer.Serialize(comparison, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        });
    }

    private static string HashEntry(Stream stream, PkgInfo info, PkgEntry entry, IKeyProvider keys,
        CancellationToken cancellationToken, Action<long> report)
    {
        using var sink = new HashingSink(cancellationToken, report);
        PkgReader.ExtractEntry(stream, info.Header, entry, sink, keys, cancellationToken);
        return Convert.ToHexString(sink.GetHash()).ToLowerInvariant();
    }

    private static PackageComparisonSide Describe(string path, PkgInfo info) => new(
        path,
        info.Header.PlatformDisplay,
        EmptyToNull(info.ContentId.Raw),
        info.Sfo?.TitleId ?? info.ContentId.TitleId,
        info.Sfo?.Title ?? info.ContentId.Name,
        info.Sfo?.AppVersion ?? info.Sfo?.Version,
        info.Sfo?.Category,
        PackageLibraryMatcher.Classify(info.Metadata.ContentType, info.Sfo?.Category),
        info.FileCount,
        new FileInfo(path).Length);

    private static IReadOnlyList<PackageSfoDifference> CompareSfo(SfoTable? baseSfo, SfoTable? targetSfo)
    {
        var baseValues = baseSfo?.Entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var targetValues = targetSfo?.Entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return baseValues.Keys.Concat(targetValues.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key =>
            {
                bool hasBase = baseValues.TryGetValue(key, out string? baseValue);
                bool hasTarget = targetValues.TryGetValue(key, out string? targetValue);
                string change = !hasBase ? "Added" : !hasTarget ? "Removed" : baseValue == targetValue ? "Unchanged" : "Modified";
                return new PackageSfoDifference(key, change, baseValue, targetValue);
            })
            .ToArray();
    }

    private static IReadOnlyList<string> BuildWarnings(PkgInfo baseInfo, PkgInfo targetInfo)
    {
        var warnings = new List<string>();
        string? baseTitleId = baseInfo.Sfo?.TitleId ?? baseInfo.ContentId.TitleId;
        string? targetTitleId = targetInfo.Sfo?.TitleId ?? targetInfo.ContentId.TitleId;
        if (!string.Equals(baseTitleId, targetTitleId, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Title IDs differ ({baseTitleId ?? "unknown"} vs {targetTitleId ?? "unknown"}); these packages may be unrelated or from different regions, so overlay creation is disabled.");
        if (baseInfo.Header.Platform != targetInfo.Header.Platform)
            warnings.Add($"Platforms differ ({baseInfo.Header.PlatformDisplay} vs {targetInfo.Header.PlatformDisplay}).");
        string? baseVersion = baseInfo.Sfo?.AppVersion ?? baseInfo.Sfo?.Version;
        string? targetVersion = targetInfo.Sfo?.AppVersion ?? targetInfo.Sfo?.Version;
        if (CompareVersion(targetVersion, baseVersion) < 0)
            warnings.Add($"The target version ({targetVersion ?? "unknown"}) appears older than the base ({baseVersion ?? "unknown"}). Consider swapping the packages.");
        if (!targetInfo.Header.IsPs3)
            warnings.Add("Compact overlay package creation is currently limited to PS3 packages; comparison still works.");
        if (targetInfo.Sfo is null)
            warnings.Add("The target has no readable PARAM.SFO, so a patch-style overlay package cannot be created.");
        return warnings;
    }

    private static byte[] BuildPatchSfo(SfoTable targetSfo)
    {
        var entries = targetSfo.Entries.Select(entry => entry.Key == "CATEGORY" ? entry.WithValue("GP") : entry).ToList();
        if (!entries.Any(entry => entry.Key == "CATEGORY"))
        {
            entries.Add(new SfoEntry
            {
                Key = "CATEGORY",
                Format = SfoFormat.Utf8,
                Value = "GP",
                MaxLength = 3,
            });
        }
        return SfoWriter.Write(entries);
    }

    private static void VerifyOverlayPayloads(string packagePath,
        IReadOnlyList<(PackageFileDifference Difference, string StagedPath)> staged,
        IKeyProvider keys, CancellationToken cancellationToken, IProgress<PkgOperationProgress>? progress)
    {
        using var package = File.OpenRead(packagePath);
        PkgInfo info = PkgReader.Read(package, keys);
        if (!string.Equals(info.Sfo?.Category, "GP", StringComparison.Ordinal))
            throw new PkgFormatException("The generated overlay PARAM.SFO did not retain patch category GP.");

        var entries = info.Entries.Where(entry => entry.IsFile)
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var expectedPaths = staged.Select(item => item.Difference.Path)
            .Append("PARAM.SFO").ToHashSet(StringComparer.Ordinal);
        if (!entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedPaths))
            throw new PkgFormatException("The generated overlay contains an unexpected or missing file entry.");

        long total = staged.Sum(item => checked((long)(item.Difference.TargetSize ?? 0)));
        long completed = 0;
        foreach ((PackageFileDifference difference, _) in staged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(difference.Path, out PkgEntry? entry))
                throw new PkgFormatException($"The generated overlay is missing {difference.Path}.");
            long itemBase = completed;
            string hash = HashEntry(package, info, entry, keys, cancellationToken, bytes =>
                progress?.Report(new PkgOperationProgress(itemBase + bytes, total, $"Verifying {difference.Path}")));
            if (!string.Equals(hash, difference.TargetSha256, StringComparison.Ordinal))
                throw new PkgFormatException($"The generated overlay payload for {difference.Path} does not match the target package.");
            completed += checked((long)entry.FileSize);
        }
    }

    private static void AddParentDirectories(string path, ISet<string> directories)
    {
        string normalized = path.Replace('\\', '/').Trim('/');
        int slash = normalized.LastIndexOf('/');
        while (slash > 0)
        {
            directories.Add(normalized[..slash]);
            slash = normalized.LastIndexOf('/', slash - 1);
        }
    }

    private static long CheckedFileBytes(IEnumerable<PkgEntry> entries) =>
        entries.Aggregate(0L, (total, entry) => checked(total + checked((long)entry.FileSize)));

    private static int CompareVersion(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;
        int[] leftParts = left.Split('.', '-', '_').Select(part => int.TryParse(part, out int value) ? value : 0).ToArray();
        int[] rightParts = right.Split('.', '-', '_').Select(part => int.TryParse(part, out int value) ? value : 0).ToArray();
        for (int index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            int comparison = (index < leftParts.Length ? leftParts[index] : 0)
                .CompareTo(index < rightParts.Length ? rightParts[index] : 0);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class HashingSink : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly CancellationToken _cancellationToken;
        private readonly Action<long> _report;
        private long _written;

        public HashingSink(CancellationToken cancellationToken, Action<long> report)
        {
            _cancellationToken = cancellationToken;
            _report = report;
        }

        public byte[] GetHash() => _hash.GetHashAndReset();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _hash.AppendData(buffer);
            _written += buffer.Length;
            _report(_written);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
