using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PkgLens.Core.Shared.Keys;

namespace PkgLens.Core.Shared;

public enum PackageOrganizerAction
{
    Organize,
    SkipExactDuplicate,
    SkipUnreadable,
}

public enum PackageOrganizerMode
{
    Copy,
    Move,
}

public enum PackageOrganizerStatus
{
    Pending,
    Copied,
    Moved,
    AlreadyPresent,
    Skipped,
    Failed,
}

public sealed class PackageOrganizerItem
{
    public required string SourcePath { get; init; }
    public required string RelativePath { get; init; }
    public required PackageScanRow Package { get; init; }
    public required string Sha256 { get; init; }
    public required PackageOrganizerAction Action { get; set; }
    public string? DestinationPath { get; set; }
    public bool IsExactDuplicate { get; set; }
    public bool IsContentIdDuplicate { get; set; }
    public bool IsSupersededUpdate { get; set; }
    public int ExactCopyCount { get; set; } = 1;
    public string? DuplicateOf { get; set; }
    public string? SupersededByVersion { get; set; }
    public PackageOrganizerStatus Status { get; set; } = PackageOrganizerStatus.Pending;
    public string? Result { get; set; }

    [JsonIgnore]
    public string Relationship => DescribeRelationship();

    private string DescribeRelationship()
    {
        if (Package.Failed) return "Unreadable";
        if (IsExactDuplicate) return $"Exact SHA-256 duplicate of {Path.GetFileName(DuplicateOf)}";
        var details = new List<string>();
        if (IsContentIdDuplicate)
            details.Add($"Same content ID; different data from {Path.GetFileName(DuplicateOf)}");
        if (IsSupersededUpdate)
            details.Add($"Superseded by version {SupersededByVersion}");
        if (ExactCopyCount > 1)
            details.Add($"Original of {ExactCopyCount} identical copies");
        if (details.Count == 0 && Package.Role == PackageLibraryRole.Update)
            details.Add("Latest update");
        return details.Count == 0 ? "Unique" : string.Join(" · ", details);
    }
}

public sealed class PackageOrganizerPlan
{
    public int FormatVersion { get; init; } = 1;
    public required string SourceDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public bool Recursive { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<PackageOrganizerItem> Items { get; init; } = new();

    public int ExactDuplicateCount => Items.Count(item => item.IsExactDuplicate);
    public int ContentIdDuplicateCount => Items.Count(item => item.IsContentIdDuplicate);
    public int SupersededUpdateCount => Items.Count(item => item.IsSupersededUpdate);
    public int UnreadableCount => Items.Count(item => item.Package.Failed);
    public int OrganizeCount => Items.Count(item => item.Action == PackageOrganizerAction.Organize);
}

public sealed record PackageOrganizerProgress(
    int Completed, int Total, string Source, string Stage, long BytesCompleted = 0, long BytesTotal = 0);

/// <summary>Builds and applies a preview-first, hash-verified package library organization plan.</summary>
public static class PackageOrganizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static PackageOrganizerPlan Analyze(string sourceDirectory, string outputDirectory,
        bool recursive, IKeyProvider keys, CancellationToken cancellationToken = default,
        IProgress<PackageOrganizerProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(keys);
        string sourceRoot = Path.GetFullPath(sourceDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Organizer source folder was not found: {sourceRoot}");
        if (PathComparer.Equals(TrimDirectory(sourceRoot), TrimDirectory(outputRoot)))
            throw new ArgumentException("The organizer output folder must differ from the source folder.",
                nameof(outputDirectory));

        SearchOption search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] files = Directory.EnumerateFiles(sourceRoot, "*", search)
            .Where(path => Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsWithin(path, outputRoot))
            .OrderBy(path => path, PathComparer)
            .ToArray();

        var plan = new PackageOrganizerPlan
        {
            SourceDirectory = sourceRoot,
            OutputDirectory = outputRoot,
            Recursive = recursive,
        };

        for (int index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = files[index];
            progress?.Report(new PackageOrganizerProgress(index, files.Length,
                Path.GetRelativePath(sourceRoot, path), "Hashing and classifying"));
            string hash = HashFile(path, cancellationToken);
            PackageScanRow package = PackageScanner.Inspect(path, keys);
            plan.Items.Add(new PackageOrganizerItem
            {
                SourcePath = path,
                RelativePath = Path.GetRelativePath(sourceRoot, path),
                Package = package,
                Sha256 = hash,
                Action = package.Failed ? PackageOrganizerAction.SkipUnreadable : PackageOrganizerAction.Organize,
                Status = package.Failed ? PackageOrganizerStatus.Skipped : PackageOrganizerStatus.Pending,
                Result = package.Failed ? package.Note : null,
            });
            progress?.Report(new PackageOrganizerProgress(index + 1, files.Length,
                Path.GetRelativePath(sourceRoot, path), "Classified"));
        }

        MarkDuplicates(plan.Items);
        MarkSupersededUpdates(plan.Items);
        AssignDestinations(plan);
        return plan;
    }

    public static void Apply(PackageOrganizerPlan plan, PackageOrganizerMode mode,
        CancellationToken cancellationToken = default,
        IProgress<PackageOrganizerProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
        PackageOrganizerItem[] work = plan.Items
            .Where(item => item.Action == PackageOrganizerAction.Organize)
            .ToArray();

        for (int index = 0; index < work.Length; index++)
        {
            PackageOrganizerItem item = work[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = item.DestinationPath!;
                if (File.Exists(destination))
                {
                    string existingHash = HashFile(destination, cancellationToken);
                    if (!existingHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Destination already exists with different data: {destination}");
                    item.Status = PackageOrganizerStatus.AlreadyPresent;
                    item.Result = "Verified identical file already present";
                    if (mode == PackageOrganizerMode.Move && !PathComparer.Equals(item.SourcePath, destination))
                    {
                        File.Delete(item.SourcePath);
                        item.Status = PackageOrganizerStatus.Moved;
                        item.Result = "Existing destination verified; source removed";
                    }
                    continue;
                }

                CopyVerified(item, destination, cancellationToken, (copied, total) =>
                    progress?.Report(new PackageOrganizerProgress(index, work.Length,
                        item.RelativePath, mode == PackageOrganizerMode.Move ? "Copying before verified move" : "Copying",
                        copied, total)));
                if (mode == PackageOrganizerMode.Move)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(item.SourcePath);
                    item.Status = PackageOrganizerStatus.Moved;
                    item.Result = "Copied, SHA-256 verified, then removed source";
                }
                else
                {
                    item.Status = PackageOrganizerStatus.Copied;
                    item.Result = "Copied and SHA-256 verified";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                item.Status = PackageOrganizerStatus.Pending;
                item.Result = "Cancelled before completion";
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                item.Status = PackageOrganizerStatus.Failed;
                item.Result = ex.Message;
            }
            finally
            {
                progress?.Report(new PackageOrganizerProgress(index + 1, work.Length,
                    item.RelativePath, item.Result ?? item.Status.ToString()));
            }
        }

        foreach (PackageOrganizerItem skipped in plan.Items.Where(item => item.Action != PackageOrganizerAction.Organize))
            skipped.Status = PackageOrganizerStatus.Skipped;
        WriteTextAtomic(Path.Combine(plan.OutputDirectory, "package-organizer-report.json"), ToJson(plan));
    }

    public static string ToJson(PackageOrganizerPlan plan) => JsonSerializer.Serialize(plan, JsonOptions);

    private static void MarkDuplicates(List<PackageOrganizerItem> items)
    {
        foreach (PackageOrganizerItem[] group in items.GroupBy(item => item.Sha256, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderBy(item => item.SourcePath, PathComparer).ToArray())
                     .Where(group => group.Length > 1))
        {
            foreach (PackageOrganizerItem item in group) item.ExactCopyCount = group.Length;
            PackageOrganizerItem canonical = group[0];
            foreach (PackageOrganizerItem duplicate in group.Skip(1))
            {
                duplicate.IsExactDuplicate = true;
                duplicate.DuplicateOf = canonical.SourcePath;
                duplicate.Action = PackageOrganizerAction.SkipExactDuplicate;
                duplicate.Status = PackageOrganizerStatus.Skipped;
                duplicate.Result = "Exact duplicate retained at source; not copied or deleted";
            }
        }

        foreach (PackageOrganizerItem[] group in items
                     .Where(item => !string.IsNullOrWhiteSpace(item.Package.ContentId))
                     .GroupBy(item => item.Package.ContentId!, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderBy(item => item.SourcePath, PathComparer).ToArray())
                     .Where(group => group.Select(item => item.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            PackageOrganizerItem canonical = group[0];
            foreach (PackageOrganizerItem variant in group.Skip(1).Where(item => !item.IsExactDuplicate))
            {
                variant.IsContentIdDuplicate = true;
                variant.DuplicateOf = canonical.SourcePath;
            }
        }
    }

    private static void MarkSupersededUpdates(List<PackageOrganizerItem> items)
    {
        foreach (IGrouping<string, PackageOrganizerItem> group in items
                     .Where(item => !item.Package.Failed && item.Package.Role == PackageLibraryRole.Update &&
                                    !string.IsNullOrWhiteSpace(item.Package.TitleId))
                     .GroupBy(item => $"{item.Package.Platform}|{item.Package.TitleId}|{item.Package.Region}",
                         StringComparer.OrdinalIgnoreCase))
        {
            PackageOrganizerItem[] versioned = group.Where(item => TryVersion(item.Package.Version, out _)).ToArray();
            if (versioned.Length < 2) continue;
            int[] newest = versioned.Select(item => ParseVersion(item.Package.Version!))
                .OrderByDescending(version => version, VersionArrayComparer.Instance).First();
            string newestText = versioned.First(item =>
                VersionArrayComparer.Instance.Compare(ParseVersion(item.Package.Version!), newest) == 0).Package.Version!;
            foreach (PackageOrganizerItem older in versioned.Where(item =>
                         VersionArrayComparer.Instance.Compare(ParseVersion(item.Package.Version!), newest) < 0))
            {
                older.IsSupersededUpdate = true;
                older.SupersededByVersion = newestText;
            }
        }
    }

    private static void AssignDestinations(PackageOrganizerPlan plan)
    {
        var used = new HashSet<string>(PathComparer);
        foreach (PackageOrganizerItem item in plan.Items.Where(item => item.Action == PackageOrganizerAction.Organize))
        {
            string platform = item.Package.Platform.Equals("PSVita", StringComparison.OrdinalIgnoreCase)
                ? "Vita"
                : Sanitize(item.Package.Platform);
            string titleId = string.IsNullOrWhiteSpace(item.Package.TitleId)
                ? item.Package.ContentId ?? "Unknown title"
                : item.Package.TitleId;
            string title = string.IsNullOrWhiteSpace(item.Package.Title) ? string.Empty : item.Package.Title;
            string titleFolder = Sanitize(string.IsNullOrEmpty(title)
                ? titleId
                : $"{titleId} - {title}", maximumLength: 110);
            string role = item.Package.Role switch
            {
                PackageLibraryRole.Base => "Base",
                PackageLibraryRole.Update => item.IsSupersededUpdate
                    ? Path.Combine("Updates", "Superseded")
                    : "Updates",
                PackageLibraryRole.Dlc => "DLC",
                PackageLibraryRole.Theme => "Themes",
                _ => "Other",
            };
            string directory = Path.Combine(plan.OutputDirectory, platform, titleFolder, role);
            string destination = Path.Combine(directory, Path.GetFileName(item.SourcePath));
            if (!used.Add(destination))
            {
                string extension = Path.GetExtension(destination);
                string name = Path.GetFileNameWithoutExtension(destination);
                destination = Path.Combine(directory, $"{name} [{item.Sha256[..8]}]{extension}");
                int suffix = 2;
                while (!used.Add(destination))
                    destination = Path.Combine(directory, $"{name} [{item.Sha256[..8]}-{suffix++}]{extension}");
            }
            item.DestinationPath = destination;
        }
    }

    private static void CopyVerified(PackageOrganizerItem item, string destination,
        CancellationToken cancellationToken, Action<long, long> progress)
    {
        string directory = Path.GetDirectoryName(destination) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".pkglens-organize-{Guid.NewGuid():N}.tmp");
        try
        {
            using var input = File.OpenRead(item.SourcePath);
            using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                copied += read;
                progress(copied, input.Length);
            }
            output.Flush(flushToDisk: true);
            output.Dispose();
            string copiedHash = HashFile(temporary, cancellationToken);
            if (!copiedHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Copied file hash mismatch for {Path.GetFileName(item.SourcePath)}.");
            File.Move(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        using var input = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidatePlan(PackageOrganizerPlan plan)
    {
        string sourceRoot = Path.GetFullPath(plan.SourceDirectory);
        string outputRoot = Path.GetFullPath(plan.OutputDirectory);
        foreach (PackageOrganizerItem item in plan.Items)
        {
            string expectedSource = Path.GetFullPath(Path.Combine(sourceRoot, item.RelativePath));
            if (!IsWithin(expectedSource, sourceRoot) || !PathComparer.Equals(expectedSource, Path.GetFullPath(item.SourcePath)))
                throw new PkgFormatException($"Organizer source is outside the selected source folder: {item.SourcePath}");
            if (item.Action == PackageOrganizerAction.Organize &&
                (item.DestinationPath is null || !IsWithin(item.DestinationPath, outputRoot)))
                throw new PkgFormatException($"Organizer destination is outside the selected output folder: {item.DestinationPath}");
        }
    }

    private static bool TryVersion(string? text, out int[] version)
    {
        version = string.IsNullOrWhiteSpace(text) ? Array.Empty<int>() : ParseVersion(text);
        return version.Length > 0;
    }

    private static int[] ParseVersion(string text) => text.Split('.', '-', '_')
        .Select(part => new string(part.TakeWhile(char.IsDigit).ToArray()))
        .Where(part => part.Length > 0)
        .Select(part => int.TryParse(part, out int value) ? value : 0)
        .ToArray();

    private static string Sanitize(string value, int maximumLength = 80)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        clean = clean.Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "Unknown";
        if (clean.Length > maximumLength) clean = clean[..maximumLength].TrimEnd();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        return reserved.Contains(clean, StringComparer.OrdinalIgnoreCase) ? "_" + clean : clean;
    }

    private static void WriteTextAtomic(string path, string content)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".pkglens-organizer-report-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = TrimDirectory(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, PathComparison);
    }

    private static string TrimDirectory(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class VersionArrayComparer : IComparer<int[]>
    {
        public static readonly VersionArrayComparer Instance = new();
        public int Compare(int[]? left, int[]? right)
        {
            left ??= Array.Empty<int>();
            right ??= Array.Empty<int>();
            for (int index = 0; index < Math.Max(left.Length, right.Length); index++)
            {
                int comparison = (index < left.Length ? left[index] : 0)
                    .CompareTo(index < right.Length ? right[index] : 0);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }
}
