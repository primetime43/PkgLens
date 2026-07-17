namespace PkgLens.Core.Ps3.Npd;

public sealed record RapDiscoveryCandidate(
    string ContentId,
    string Path,
    long Size,
    bool IsValid,
    string? Error);

/// <summary>Finds content-ID-named RAP files beside an input and in user-selected folders.</summary>
public static class RapDiscovery
{
    public static IReadOnlyList<RapDiscoveryCandidate> Find(
        IEnumerable<string> contentIds,
        string? sourcePath = null,
        IEnumerable<string>? searchDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(contentIds);

        var requested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string contentId in contentIds.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            RapStore.ValidateContentId(contentId);
            requested.TryAdd(contentId, contentId);
        }
        if (requested.Count == 0)
            return Array.Empty<RapDiscoveryCandidate>();

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string fullSource = Path.GetFullPath(sourcePath);
            string? nearby = Directory.Exists(fullSource) ? fullSource : Path.GetDirectoryName(fullSource);
            if (nearby is not null)
                roots.Add(nearby);
        }
        if (searchDirectories is not null)
            roots.AddRange(searchDirectories.Where(value => !string.IsNullOrWhiteSpace(value)));

        var candidates = new List<RapDiscoveryCandidate>();
        var visitedRoots = new HashSet<string>(PathComparer);
        var visitedFiles = new HashSet<string>(PathComparer);
        foreach (string rootValue in roots)
        {
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootValue)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!visitedRoots.Add(root) || !Directory.Exists(root))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetExtension(path).Equals(".rap", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in files.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string contentId = Path.GetFileNameWithoutExtension(path);
                if (!requested.TryGetValue(contentId, out string? requestedContentId))
                    continue;

                string fullPath = Path.GetFullPath(path);
                if (!visitedFiles.Add(fullPath))
                    continue;

                long size;
                string? error = null;
                try
                {
                    size = new FileInfo(fullPath).Length;
                    if (size != 16)
                        error = $"Expected 16 bytes, found {size}.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    size = 0;
                    error = ex.Message;
                }
                candidates.Add(new RapDiscoveryCandidate(requestedContentId, fullPath, size, error is null, error));
            }
        }
        return candidates;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
