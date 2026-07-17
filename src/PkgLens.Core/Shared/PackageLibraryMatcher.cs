using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public enum PackageLibraryRole
{
    Unknown,
    Base,
    Update,
    Dlc,
    Theme,
    Other,
}

public enum PackageLibraryWarningKind
{
    MissingBase,
    RegionMismatch,
}

public sealed record PackageLibraryWarning(
    PackageLibraryWarningKind Kind,
    string GroupKey,
    string Message,
    IReadOnlyList<string> Files);

public sealed class PackageLibraryGroup
{
    public required string Key { get; init; }
    public string? TitleId { get; init; }
    public string? Title { get; init; }
    public required string Region { get; init; }
    public IReadOnlyList<PackageScanRow> Packages { get; init; } = Array.Empty<PackageScanRow>();
    public bool HasBase => Packages.Any(package => package.Role == PackageLibraryRole.Base);
    public int UpdateCount => Packages.Count(package => package.Role == PackageLibraryRole.Update);
    public int DlcCount => Packages.Count(package => package.Role == PackageLibraryRole.Dlc);
}

public sealed class PackageLibraryReport
{
    public IReadOnlyList<PackageScanRow> Packages { get; init; } = Array.Empty<PackageScanRow>();
    public IReadOnlyList<PackageLibraryGroup> Groups { get; init; } = Array.Empty<PackageLibraryGroup>();
    public IReadOnlyList<PackageLibraryWarning> Warnings { get; init; } = Array.Empty<PackageLibraryWarning>();
    public int MissingBaseCount => Warnings.Count(warning => warning.Kind == PackageLibraryWarningKind.MissingBase);
    public int RegionMismatchCount => Warnings.Count(warning => warning.Kind == PackageLibraryWarningKind.RegionMismatch);
}

public static class PackageLibraryMatcher
{
    public static PackageLibraryReport Analyze(IEnumerable<PackageScanRow> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        PackageScanRow[] rows = packages.ToArray();
        PackageLibraryGroup[] groups = rows.Where(row => !row.Failed)
            .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateGroup(group.Key, group))
            .OrderBy(group => group.TitleId ?? group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Region, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var warnings = new List<PackageLibraryWarning>();
        foreach (PackageLibraryGroup group in groups)
        {
            PackageScanRow[] dependants = group.Packages
                .Where(package => package.Role is PackageLibraryRole.Update or PackageLibraryRole.Dlc)
                .ToArray();
            if (dependants.Length > 0 && !group.HasBase)
            {
                warnings.Add(new PackageLibraryWarning(PackageLibraryWarningKind.MissingBase, group.Key,
                    $"{DisplayGroup(group)} has {DescribeDependants(dependants)} but no matching base package.",
                    dependants.Select(package => package.File).ToArray()));
            }

            string[] regions = group.Packages
                .Where(package => package.Role is PackageLibraryRole.Base or PackageLibraryRole.Update or PackageLibraryRole.Dlc)
                .Select(package => package.Region)
                .Where(region => !string.IsNullOrWhiteSpace(region) && region != "Unknown")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
            if (regions.Length > 1)
            {
                warnings.Add(new PackageLibraryWarning(PackageLibraryWarningKind.RegionMismatch, group.Key,
                    $"{DisplayGroup(group)} mixes regions: {string.Join(", ", regions)}.",
                    group.Packages.Select(package => package.File).ToArray()));
            }

            foreach (PackageScanRow package in group.Packages.Where(HasContentTitleRegionMismatch))
            {
                warnings.Add(new PackageLibraryWarning(PackageLibraryWarningKind.RegionMismatch, group.Key,
                    $"{Path.GetFileName(package.File)} has conflicting content-ID and title-ID regions.",
                    new[] { package.File }));
            }
        }

        PackageLibraryGroup[] bases = groups.Where(group => group.HasBase).ToArray();
        foreach (PackageLibraryGroup group in groups.Where(group => !group.HasBase &&
                     group.Packages.Any(package => package.Role is PackageLibraryRole.Update or PackageLibraryRole.Dlc)))
        {
            string normalizedTitle = NormalizeTitle(group.Title);
            if (normalizedTitle.Length == 0)
                continue;
            PackageLibraryGroup? possibleBase = bases.FirstOrDefault(candidate =>
                NormalizeTitle(candidate.Title) == normalizedTitle &&
                !string.Equals(candidate.TitleId, group.TitleId, StringComparison.OrdinalIgnoreCase));
            if (possibleBase is null)
                continue;
            warnings.Add(new PackageLibraryWarning(PackageLibraryWarningKind.RegionMismatch, group.Key,
                $"{DisplayGroup(group)} may belong to {DisplayGroup(possibleBase)}, but their title IDs/regions differ.",
                group.Packages.Select(package => package.File)
                    .Concat(possibleBase.Packages.Select(package => package.File)).ToArray()));
        }

        PackageScanRow[] ordered = groups.SelectMany(group => group.Packages)
            .Concat(rows.Where(row => row.Failed))
            .ToArray();
        return new PackageLibraryReport { Packages = ordered, Groups = groups, Warnings = warnings };
    }

    public static PackageLibraryRole Classify(PkgContentType? contentType, string? category)
    {
        string normalizedCategory = (category ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedCategory == "GP" || contentType == PkgContentType.VitaPsmUpdate)
            return PackageLibraryRole.Update;
        if (normalizedCategory == "AC" || contentType is PkgContentType.VitaDlc or PkgContentType.Psp2Ac)
            return PackageLibraryRole.Dlc;
        if (normalizedCategory == "TH" || contentType is PkgContentType.Theme or PkgContentType.VitaTheme)
            return PackageLibraryRole.Theme;
        if (contentType == PkgContentType.GameData)
            return PackageLibraryRole.Update;
        if (contentType is PkgContentType.GameExec or PkgContentType.Psp or PkgContentType.PspGo or
            PkgContentType.MiniS or PkgContentType.Ps1Emu or PkgContentType.Ps2Classic or
            PkgContentType.Psp2Gd or PkgContentType.VitaApp or PkgContentType.VitaPsm ||
            normalizedCategory is "DG" or "HG" or "GD" or "UG" or "MG" or "EG")
            return PackageLibraryRole.Base;
        return string.IsNullOrEmpty(normalizedCategory) && contentType is null
            ? PackageLibraryRole.Unknown
            : PackageLibraryRole.Other;
    }

    public static string ResolveRegion(string? contentId, string? titleId)
    {
        string? titleRegion = RegionFromTitleId(titleId);
        string? contentRegion = RegionFromContentId(contentId);
        return titleRegion ?? contentRegion ?? "Unknown";
    }

    public static string ToJson(PackageLibraryReport report) => JsonSerializer.Serialize(report,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });

    public static string ToCsv(PackageLibraryReport report)
    {
        var warningsByFile = report.Warnings.SelectMany(warning => warning.Files.Select(file => (file, warning.Message)))
            .GroupBy(item => item.file, PathComparer)
            .ToDictionary(group => group.Key, group => string.Join(" | ", group.Select(item => item.Message).Distinct()), PathComparer);
        var text = new StringBuilder();
        text.Append("file,title_id,region,content_id,title,version,category,content_type,role,size_bytes,warning\n");
        foreach (PackageScanRow row in report.Packages)
        {
            warningsByFile.TryGetValue(row.File, out string? warning);
            text.Append(string.Join(',', Csv(row.File), Csv(row.TitleId), Csv(row.Region), Csv(row.ContentId),
                Csv(row.Title), Csv(row.Version), Csv(row.Category), Csv(row.ContentType), row.Role.ToString(),
                row.Size.ToString(), Csv(warning))).Append('\n');
        }
        return text.ToString();
    }

    private static PackageLibraryGroup CreateGroup(string key, IEnumerable<PackageScanRow> packages)
    {
        PackageScanRow[] rows = packages.OrderBy(package => RoleOrder(package.Role))
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.ContentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] regions = rows.Select(package => package.Region ?? "Unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new PackageLibraryGroup
        {
            Key = key,
            TitleId = rows.Select(package => package.TitleId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Title = rows.Select(package => package.Title).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Region = regions.Length == 1 ? regions[0] : $"Mixed ({string.Join(", ", regions)})",
            Packages = rows,
        };
    }

    private static string GroupKey(PackageScanRow row) =>
        !string.IsNullOrWhiteSpace(row.TitleId) ? row.TitleId :
        !string.IsNullOrWhiteSpace(row.ContentId) ? row.ContentId : row.File;

    private static bool HasContentTitleRegionMismatch(PackageScanRow package)
    {
        string? content = RegionFromContentId(package.ContentId);
        string? title = RegionFromTitleId(package.TitleId);
        return content is not null && title is not null && !content.Equals(title, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RegionFromContentId(string? contentId) => FirstRegionCharacter(contentId);

    private static string? RegionFromTitleId(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId)) return null;
        string value = titleId.Trim().ToUpperInvariant();
        if (value.StartsWith("PCSE") || value.StartsWith("PCSA")) return "US";
        if (value.StartsWith("PCSB") || value.StartsWith("PCSF")) return "Europe";
        if (value.StartsWith("PCSG")) return "Japan";
        if (value.StartsWith("PCSC") || value.StartsWith("PCSD") || value.StartsWith("PCSH")) return "Asia";
        return value.Length > 2 ? RegionCharacter(value[2]) : null;
    }

    private static string? FirstRegionCharacter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : RegionCharacter(char.ToUpperInvariant(value.Trim()[0]));

    private static string? RegionCharacter(char value) => value switch
    {
        'U' => "US",
        'E' => "Europe",
        'J' => "Japan",
        'A' or 'H' => "Asia",
        'K' => "Korea",
        _ => null,
    };

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        string value = title.ToUpperInvariant().Replace("UPDATE", string.Empty)
            .Replace("ADD-ON", string.Empty).Replace("ADDON", string.Empty).Replace("DLC", string.Empty);
        return new string(value.Where(char.IsLetterOrDigit).ToArray());
    }

    private static int RoleOrder(PackageLibraryRole role) => role switch
    {
        PackageLibraryRole.Base => 0,
        PackageLibraryRole.Update => 1,
        PackageLibraryRole.Dlc => 2,
        PackageLibraryRole.Theme => 3,
        PackageLibraryRole.Other => 4,
        _ => 5,
    };

    private static string DescribeDependants(IReadOnlyCollection<PackageScanRow> packages)
    {
        int updates = packages.Count(package => package.Role == PackageLibraryRole.Update);
        int dlc = packages.Count(package => package.Role == PackageLibraryRole.Dlc);
        return string.Join(" and ", new[]
        {
            updates > 0 ? $"{updates} update(s)" : null,
            dlc > 0 ? $"{dlc} DLC package(s)" : null,
        }.Where(value => value is not null));
    }

    private static string DisplayGroup(PackageLibraryGroup group) =>
        group.TitleId is null ? group.Title ?? group.Key : $"{group.TitleId} ({group.Title ?? "unknown title"})";

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.AsSpan().IndexOfAny(",\"\n\r") < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
