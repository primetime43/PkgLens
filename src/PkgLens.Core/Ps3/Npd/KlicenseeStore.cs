using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PkgLens.Core.Ps3.Npd;

/// <summary>Non-secret metadata for one locally stored klicensee mapping.</summary>
public sealed record KlicenseeStoreEntry(
    string Id,
    string? ContentId,
    string TitleId,
    string? FileName,
    uint? LicenseType,
    string Source,
    DateTimeOffset AddedUtc,
    string Fingerprint);

/// <summary>A resolved klicensee and the non-secret entry metadata that selected it.</summary>
public sealed record KlicenseeResolution(
    byte[] Klicensee,
    string DatabasePath,
    KlicenseeStoreEntry Entry);

/// <summary>Summary of an imported annotated text file or legacy klicensee.ini file.</summary>
public sealed record KlicenseeImportResult(
    int LinesRead,
    int Imported,
    int Replaced,
    int Skipped,
    IReadOnlyList<string> Errors);

/// <summary>
/// Stores user-supplied 16-byte NPDRM klicensees in a local JSON database. Listings expose only
/// mapping metadata and a one-way fingerprint; raw keys are returned only by <see cref="Find"/>.
/// </summary>
public static partial class KlicenseeStore
{
    public const string EnvVar = "PKGLENS_KLICENSEES";
    private const int SchemaVersion = 1;
    private static readonly object Sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(EnvVar);
            return !string.IsNullOrWhiteSpace(configured)
                ? Path.GetFullPath(configured)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".pkglens", "klicensees.json");
        }
    }

    public static string DatabasePath(string? path = null) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? DefaultPath : path);

    /// <summary>
    /// Resolves a key using the narrowest unambiguous mapping: content-id/file, content-id,
    /// title-id/file, then title-id. A title-wide mapping is rejected when competing keys exist.
    /// </summary>
    public static KlicenseeResolution? Find(string contentId, string? fileName = null,
        uint? licenseType = null, string? databasePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        string normalizedContentId = contentId.Trim().ToUpperInvariant();
        string titleId = ExtractTitleId(normalizedContentId)
            ?? throw new ArgumentException("The content ID does not contain a PS3 title ID.", nameof(contentId));
        string? normalizedFileName = NormalizeFileName(fileName);
        string path = DatabasePath(databasePath);

        lock (Sync)
        {
            StoreDocument document = Load(path);
            var ranked = document.Entries
                .Select(entry => (Entry: entry, Rank: MatchRank(entry, normalizedContentId, titleId,
                    normalizedFileName, licenseType)))
                .Where(candidate => candidate.Rank >= 0)
                .ToArray();
            if (ranked.Length == 0) return null;

            int bestRank = ranked.Min(candidate => candidate.Rank);
            StoreEntry[] best = ranked.Where(candidate => candidate.Rank == bestRank)
                .Select(candidate => candidate.Entry).ToArray();
            string[] distinctKeys = best.Select(entry => entry.Klicensee.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal).ToArray();
            if (distinctKeys.Length != 1) return null;

            StoreEntry selected = best.OrderByDescending(entry => entry.AddedUtc).First();
            return new KlicenseeResolution(Convert.FromHexString(selected.Klicensee), path, ToPublic(selected));
        }
    }

    /// <summary>Installs or updates an exact content-id mapping and returns its non-secret metadata.</summary>
    public static KlicenseeStoreEntry Install(string contentId, string? fileName, uint? licenseType,
        byte[] klicensee, string source, string? databasePath = null, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(klicensee);
        if (klicensee.Length != 16)
            throw new ArgumentException("A klicensee must be 16 bytes.", nameof(klicensee));
        RapStore.ValidateContentId(contentId);
        string normalizedContentId = contentId.Trim().ToUpperInvariant();
        string titleId = ExtractTitleId(normalizedContentId)
            ?? throw new ArgumentException("The content ID does not contain a PS3 title ID.", nameof(contentId));
        string path = DatabasePath(databasePath);

        lock (Sync)
        {
            StoreDocument document = Load(path);
            var incoming = NewEntry(normalizedContentId, titleId, fileName, licenseType,
                Convert.ToHexString(klicensee), source);
            Upsert(document, incoming, overwrite, out _);
            Save(path, document);
            StoreEntry stored = document.Entries.Single(entry => entry.Id == incoming.Id);
            return ToPublic(stored);
        }
    }

    /// <summary>Lists database entries without exposing their raw klicensees.</summary>
    public static IReadOnlyList<KlicenseeStoreEntry> List(string? databasePath = null)
    {
        lock (Sync)
        {
            return Load(DatabasePath(databasePath)).Entries
                .OrderBy(entry => entry.TitleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ContentId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(ToPublic).ToArray();
        }
    }

    /// <summary>Removes an entry by its opaque id.</summary>
    public static bool Remove(string id, string? databasePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string path = DatabasePath(databasePath);
        lock (Sync)
        {
            StoreDocument document = Load(path);
            int removed = document.Entries.RemoveAll(entry => entry.Id.Equals(id, StringComparison.Ordinal));
            if (removed == 0) return false;
            Save(path, document);
            return true;
        }
    }

    /// <summary>
    /// Imports annotated lines such as "32_HEX_KEY BLUS12345 description" and legacy
    /// [klicensee] INI blocks. Unannotated key pools are intentionally skipped because they cannot
    /// be mapped safely to a title or content id.
    /// </summary>
    public static KlicenseeImportResult ImportFile(string sourcePath, string? databasePath = null,
        bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string[] lines = File.ReadAllLines(sourcePath);
        var parsed = new List<StoreEntry>();
        var errors = new List<string>();
        int skipped;

        if (lines.Any(line => line.Trim().Equals("[klicensee]", StringComparison.OrdinalIgnoreCase)))
            skipped = ParseLegacyIni(lines, Path.GetFileName(sourcePath), parsed, errors);
        else
            skipped = ParseAnnotated(lines, Path.GetFileName(sourcePath), parsed, errors);

        int imported = 0;
        int replaced = 0;
        string path = DatabasePath(databasePath);
        lock (Sync)
        {
            StoreDocument document = Load(path);
            foreach (StoreEntry entry in parsed)
            {
                try
                {
                    Upsert(document, entry, overwrite, out bool didReplace);
                    if (didReplace) replaced++;
                    else imported++;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException)
                {
                    errors.Add(ex.Message);
                    skipped++;
                }
            }
            if (imported + replaced > 0) Save(path, document);
        }

        return new KlicenseeImportResult(lines.Length, imported, replaced, skipped, errors);
    }

    public static string? ExtractTitleId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Match match = TitleIdRegex().Match(text.ToUpperInvariant());
        return match.Success ? match.Value : null;
    }

    private static int ParseAnnotated(string[] lines, string sourceName, List<StoreEntry> entries,
        List<string> errors)
    {
        int skipped = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            Match keyMatch = AnnotatedKeyRegex().Match(line);
            if (!keyMatch.Success)
            {
                skipped++;
                continue;
            }

            string key = keyMatch.Groups[1].Value.ToUpperInvariant();
            string annotation = keyMatch.Groups[2].Value;
            MatchCollection contentIds = ContentIdRegex().Matches(annotation.ToUpperInvariant());
            if (contentIds.Count > 0)
            {
                foreach (Match match in contentIds)
                {
                    string contentId = match.Value;
                    entries.Add(NewEntry(contentId, ExtractTitleId(contentId)!, null, null, key,
                        $"import:{sourceName}"));
                }
                continue;
            }

            MatchCollection titleIds = TitleIdRegex().Matches(annotation.ToUpperInvariant());
            if (titleIds.Count == 0)
            {
                skipped++;
                continue;
            }
            foreach (string titleId in titleIds.Select(match => match.Value).Distinct(StringComparer.Ordinal))
                entries.Add(NewEntry(null, titleId, null, null, key, $"import:{sourceName}"));
        }
        return skipped;
    }

    private static int ParseLegacyIni(string[] lines, string sourceName, List<StoreEntry> entries,
        List<string> errors)
    {
        int skipped = 0;
        Dictionary<string, string>? block = null;
        for (int index = 0; index <= lines.Length; index++)
        {
            string line = index < lines.Length ? lines[index].Trim() : "[/klicensee]";
            if (line.Equals("[klicensee]", StringComparison.OrdinalIgnoreCase))
            {
                if (block is not null) skipped++;
                block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (line.Equals("[/klicensee]", StringComparison.OrdinalIgnoreCase))
            {
                if (block is not null)
                {
                    ParseLegacyBlock(block, sourceName, entries, errors);
                    block = null;
                }
                continue;
            }
            if (block is null || line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                skipped++;
                continue;
            }
            block[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return skipped;
    }

    private static void ParseLegacyBlock(Dictionary<string, string> block, string sourceName,
        List<StoreEntry> entries, List<string> errors)
    {
        block.TryGetValue("productid", out string? productId);
        string? titleId = ExtractTitleId(productId);
        if (titleId is null)
        {
            errors.Add($"Skipped INI block with no valid productid: {productId ?? "(missing)"}.");
            return;
        }
        string? contentId = ContentIdRegex().IsMatch(productId?.ToUpperInvariant() ?? string.Empty)
            ? ContentIdRegex().Match(productId!.ToUpperInvariant()).Value
            : null;
        block.TryGetValue("filename", out string? fileName);
        for (uint license = 1; license <= 3; license++)
        {
            if (!block.TryGetValue($"key{license}", out string? key) || string.IsNullOrWhiteSpace(key))
                continue;
            key = Regex.Replace(key, "[^0-9A-Fa-f]", string.Empty).ToUpperInvariant();
            if (key.Length != 32)
            {
                errors.Add($"Skipped invalid key{license} for {productId}.");
                continue;
            }
            entries.Add(NewEntry(contentId, titleId, fileName, license, key, $"import:{sourceName}"));
        }
    }

    private static int MatchRank(StoreEntry entry, string contentId, string titleId, string? fileName,
        uint? licenseType)
    {
        if (licenseType is not null && entry.LicenseType is not null && entry.LicenseType != licenseType)
            return -1;
        bool fileExact = entry.FileName is not null && fileName is not null &&
                         entry.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase);
        if (entry.FileName is not null && !fileExact) return -1;

        int baseRank;
        if (entry.ContentId is not null && entry.ContentId.Equals(contentId, StringComparison.OrdinalIgnoreCase))
            baseRank = fileExact ? 0 : 2;
        else if (entry.ContentId is null && entry.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase))
            baseRank = fileExact ? 4 : 6;
        else
            return -1;

        return baseRank + (licenseType is not null && entry.LicenseType == licenseType ? 0 : 1);
    }

    private static StoreEntry NewEntry(string? contentId, string titleId, string? fileName,
        uint? licenseType, string key, string source) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ContentId = contentId?.Trim().ToUpperInvariant(),
        TitleId = titleId.Trim().ToUpperInvariant(),
        FileName = NormalizeFileName(fileName),
        LicenseType = licenseType,
        Klicensee = key.ToUpperInvariant(),
        Source = string.IsNullOrWhiteSpace(source) ? "local" : source.Trim(),
        AddedUtc = DateTimeOffset.UtcNow,
    };

    private static void Upsert(StoreDocument document, StoreEntry incoming, bool overwrite,
        out bool replaced)
    {
        if (incoming.Klicensee.Length != 32 || !incoming.Klicensee.All(Uri.IsHexDigit))
            throw new ArgumentException("A klicensee must be exactly 32 hexadecimal characters.");
        int index = document.Entries.FindIndex(entry =>
            string.Equals(entry.ContentId, incoming.ContentId, StringComparison.OrdinalIgnoreCase) &&
            entry.TitleId.Equals(incoming.TitleId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.FileName, incoming.FileName, StringComparison.OrdinalIgnoreCase) &&
            entry.LicenseType == incoming.LicenseType &&
            // A title-wide import can legitimately contain different keys for different SELF files
            // whose names were not annotated. Preserve those candidates so Find can reject an
            // unsafe ambiguous fallback instead of silently selecting the last imported key.
            (incoming.ContentId is not null ||
             entry.Klicensee.Equals(incoming.Klicensee, StringComparison.OrdinalIgnoreCase)));
        replaced = index >= 0;
        if (index < 0)
        {
            document.Entries.Add(incoming);
            return;
        }
        if (!overwrite)
            throw new IOException($"A klicensee mapping for {incoming.ContentId ?? incoming.TitleId} already exists.");
        incoming.Id = document.Entries[index].Id;
        document.Entries[index] = incoming;
    }

    private static KlicenseeStoreEntry ToPublic(StoreEntry entry)
    {
        byte[] key = Convert.FromHexString(entry.Klicensee);
        string fingerprint = Convert.ToHexString(SHA256.HashData(key))[..12];
        return new KlicenseeStoreEntry(entry.Id, entry.ContentId, entry.TitleId, entry.FileName,
            entry.LicenseType, entry.Source, entry.AddedUtc, fingerprint);
    }

    private static StoreDocument Load(string path)
    {
        if (!File.Exists(path)) return new StoreDocument();
        try
        {
            StoreDocument? document = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.SchemaVersion != SchemaVersion)
                throw new InvalidDataException("Unsupported or missing klicensee database schema version.");
            document.Entries ??= [];
            foreach (StoreEntry entry in document.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.TitleId) ||
                    entry.Klicensee.Length != 32 || !entry.Klicensee.All(Uri.IsHexDigit))
                    throw new InvalidDataException("The klicensee database contains a malformed entry.");
            }
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The klicensee database is not valid JSON: {ex.Message}", ex);
        }
    }

    private static void Save(string path, StoreDocument document)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Database path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup */ }
        }
    }

    private static string? NormalizeFileName(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileName(fileName.Trim());

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{4}\d{5}(?![A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex TitleIdRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{2}\d{4}-[A-Z0-9]{9}_\d{2}-[A-Z0-9]{16}(?![A-Z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdRegex();

    [GeneratedRegex(@"^\s*([0-9A-Fa-f]{32})\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AnnotatedKeyRegex();

    private sealed class StoreDocument
    {
        public int SchemaVersion { get; set; } = KlicenseeStore.SchemaVersion;
        public List<StoreEntry> Entries { get; set; } = [];
    }

    private sealed class StoreEntry
    {
        public string Id { get; set; } = string.Empty;
        public string? ContentId { get; set; }
        public string TitleId { get; set; } = string.Empty;
        public string? FileName { get; set; }
        public uint? LicenseType { get; set; }
        public string Klicensee { get; set; } = string.Empty;
        public string Source { get; set; } = "local";
        public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
