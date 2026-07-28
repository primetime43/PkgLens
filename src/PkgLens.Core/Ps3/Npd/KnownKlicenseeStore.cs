using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PkgLens.Core.Ps3.Npd;

/// <summary>
/// Read-only catalog of publicly documented, title-specific NPDRM klicensees bundled with PkgLens.
/// Exact content-id matches are preferred; title-wide matches are returned only when every matching
/// catalog entry agrees on one key.
/// </summary>
public static partial class KnownKlicenseeStore
{
    public const string DatabaseId = "embedded://PkgLens.Core/known-klicensees";
    private const string ResourceName = "PkgLens.Core.known-klicensees.txt";
    private static readonly Lazy<IReadOnlyList<KnownEntry>> Entries = new(Load);
    private static readonly Lazy<IReadOnlyList<KlicenseeStoreEntry>> PublicEntries =
        new(() => Entries.Value.Select(ToPublicEntry).ToArray());

    public static int Count => Entries.Value.Count;

    /// <summary>
    /// Lists the non-secret metadata for every bundled mapping. Raw klicensees are never returned.
    /// </summary>
    public static IReadOnlyList<KlicenseeStoreEntry> List() => PublicEntries.Value;

    public static KlicenseeResolution? Find(string contentId, string? fileName = null,
        uint? licenseType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        string normalizedContentId = contentId.Trim().ToUpperInvariant();
        string titleId = KlicenseeStore.ExtractTitleId(normalizedContentId)
            ?? throw new ArgumentException("The content ID does not contain a PS3 title ID.", nameof(contentId));
        string? normalizedFileName = string.IsNullOrWhiteSpace(fileName)
            ? null
            : Path.GetFileName(fileName.Trim()).ToUpperInvariant();

        var ranked = Entries.Value
            .Select(entry => (Entry: entry, Rank: MatchRank(entry, normalizedContentId, titleId,
                normalizedFileName, licenseType)))
            .Where(candidate => candidate.Rank >= 0)
            .ToArray();
        if (ranked.Length == 0) return null;

        int bestRank = ranked.Min(candidate => candidate.Rank);
        KnownEntry[] best = ranked.Where(candidate => candidate.Rank == bestRank)
            .Select(candidate => candidate.Entry).ToArray();
        string[] distinctKeys = best.Select(entry => entry.Klicensee)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (distinctKeys.Length != 1) return null;

        KnownEntry selected = best[0];
        byte[] key = Convert.FromHexString(selected.Klicensee);
        return new KlicenseeResolution(key, DatabaseId, ToPublicEntry(selected));
    }

    private static KlicenseeStoreEntry ToPublicEntry(KnownEntry entry)
    {
        byte[] key = Convert.FromHexString(entry.Klicensee);
        string fingerprint = Convert.ToHexString(SHA256.HashData(key))[..12];
        return new KlicenseeStoreEntry(entry.Id, entry.ContentId, entry.TitleId,
            entry.FileName, entry.LicenseType, "bundled catalog", DateTimeOffset.UnixEpoch,
            fingerprint);
    }

    private static int MatchRank(KnownEntry entry, string contentId, string titleId,
        string? fileName, uint? licenseType)
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

    private static IReadOnlyList<KnownEntry> Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"Bundled klicensee catalog resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        var entries = new List<KnownEntry>();
        int lineNumber = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            Match keyMatch = KeyLineRegex().Match(line);
            if (!keyMatch.Success)
                throw new InvalidDataException($"Bundled klicensee catalog line {lineNumber} is malformed.");

            string key = keyMatch.Groups[1].Value.ToUpperInvariant();
            string annotation = keyMatch.Groups[2].Value.Trim();
            if (annotation.Equals("No_klic", StringComparison.OrdinalIgnoreCase) ||
                annotation.Equals("NP_klic_free", StringComparison.OrdinalIgnoreCase) ||
                annotation.Equals("NP KLic", StringComparison.OrdinalIgnoreCase))
                continue;

            string? fileName = FileNameRegex().Matches(annotation)
                .Select(match => match.Groups[1].Value.ToUpperInvariant()).FirstOrDefault();
            string[] contentIds = ContentIdRegex().Matches(annotation.ToUpperInvariant())
                .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
            string[] titleIds = TitleIdRegex().Matches(annotation.ToUpperInvariant())
                .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();

            foreach (string contentId in contentIds)
            {
                string titleId = KlicenseeStore.ExtractTitleId(contentId)!;
                entries.Add(NewEntry(lineNumber, key, contentId, titleId, fileName));
            }
            foreach (string titleId in titleIds)
                entries.Add(NewEntry(lineNumber, key, null, titleId, fileName));
        }

        if (entries.Count == 0)
            throw new InvalidDataException("Bundled klicensee catalog contains no usable mappings.");
        return entries;
    }

    private static KnownEntry NewEntry(int lineNumber, string key, string? contentId,
        string titleId, string? fileName) => new()
    {
        Id = $"bundled-{lineNumber}-{contentId ?? titleId}-{fileName ?? "any"}",
        ContentId = contentId,
        TitleId = titleId,
        FileName = fileName,
        LicenseType = null,
        Klicensee = key,
    };

    private sealed class KnownEntry
    {
        public required string Id { get; init; }
        public string? ContentId { get; init; }
        public required string TitleId { get; init; }
        public string? FileName { get; init; }
        public uint? LicenseType { get; init; }
        public required string Klicensee { get; init; }
    }

    [GeneratedRegex(@"^([0-9A-Fa-f]{32})\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLineRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{4}\d{5}(?![A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex TitleIdRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{2}\d{4}-[A-Z0-9]{9}_\d{2}-[A-Z0-9]{16}(?![A-Z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdRegex();

    [GeneratedRegex(@"\[([^\]\s]+\.(?:self|sprx|bin))\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();
}
