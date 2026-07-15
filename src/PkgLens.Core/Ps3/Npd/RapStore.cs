namespace PkgLens.Core.Ps3.Npd;

/// <summary>One RAP library entry. RAP bytes are never exposed by listings.</summary>
public sealed record RapStoreEntry(string ContentId, string Path, long Size, bool IsValid, string? Error);

/// <summary>
/// Manages RAP license files by content id. The default library is
/// <c>~/.pkglens/raps/&lt;content-id&gt;.rap</c>; <c>PKGLENS_RAPS</c> or an explicit directory can
/// select a different library.
/// </summary>
public static class RapStore
{
    public const string EnvVar = "PKGLENS_RAPS";

    public static string DefaultDirectory
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(EnvVar);
            return !string.IsNullOrWhiteSpace(configured)
                ? Path.GetFullPath(configured)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pkglens", "raps");
        }
    }

    /// <summary>Returns the effective RAP library directory.</summary>
    public static string DirectoryPath(string? directory = null) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory);

    /// <summary>Returns the canonical path for a content id after validating it as a safe filename.</summary>
    public static string PathFor(string contentId, string? directory = null)
    {
        ValidateContentId(contentId);
        return Path.Combine(DirectoryPath(directory), contentId + ".rap");
    }

    /// <summary>Returns the 16-byte RAP for <paramref name="contentId"/>, or null if none is installed or valid.</summary>
    public static byte[]? Find(string contentId, string? directory = null)
    {
        string path = PathFor(contentId, directory);
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        return bytes.Length == 16 ? bytes : null;
    }

    /// <summary>Installs a RAP under its content id and returns the destination path.</summary>
    public static string Install(string contentId, byte[] rap, string? directory = null, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(rap);
        if (rap.Length != 16) throw new ArgumentException("A RAP must be 16 bytes.", nameof(rap));

        string path = PathFor(contentId, directory);
        string root = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(root);
        if (!overwrite && File.Exists(path))
            throw new IOException($"A RAP for '{contentId}' is already installed.");

        string temp = Path.Combine(root, $".{contentId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, rap);
            File.Move(temp, path, overwrite);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup */ }
        }
        return path;
    }

    /// <summary>Lists valid and invalid <c>*.rap</c> files without reading or returning their secret bytes.</summary>
    public static IReadOnlyList<RapStoreEntry> List(string? directory = null)
    {
        string root = Path.TrimEndingDirectorySeparator(DirectoryPath(directory));
        if (!Directory.Exists(root)) return Array.Empty<RapStoreEntry>();

        var entries = new List<RapStoreEntry>();
        foreach (string path in Directory.EnumerateFiles(root, "*.rap", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string contentId = Path.GetFileNameWithoutExtension(path);
            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries.Add(new RapStoreEntry(contentId, path, 0, false, ex.Message));
                continue;
            }

            string? error = null;
            try { ValidateContentId(contentId); }
            catch (ArgumentException ex) { error = ex.Message; }
            if (error is null && size != 16) error = $"expected 16 bytes, found {size}";
            entries.Add(new RapStoreEntry(contentId, path, size, error is null, error));
        }
        return entries;
    }

    /// <summary>Removes a RAP by content id. Returns false when no matching file exists.</summary>
    public static bool Remove(string contentId, string? directory = null)
    {
        string path = PathFor(contentId, directory);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Removes an entry returned by <see cref="List"/>, including malformed filenames.</summary>
    public static bool RemoveEntry(RapStoreEntry entry, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string root = Path.TrimEndingDirectorySeparator(DirectoryPath(directory));
        string fullPath = Path.GetFullPath(entry.Path);
        if (!Path.GetDirectoryName(fullPath)!.Equals(root, PathComparison) ||
            !Path.GetExtension(fullPath).Equals(".rap", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The RAP entry is outside the selected library.", nameof(entry));
        if (!File.Exists(fullPath)) return false;
        File.Delete(fullPath);
        return true;
    }

    /// <summary>Validates the portable filename subset used by PS3 content IDs.</summary>
    public static void ValidateContentId(string contentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        if (contentId.Length > 48 || contentId.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new ArgumentException(
                "A content ID must be 1-48 ASCII letters, digits, hyphens, or underscores.", nameof(contentId));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
