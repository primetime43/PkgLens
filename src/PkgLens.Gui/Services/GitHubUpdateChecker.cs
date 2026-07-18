using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PkgLens.Gui.Services;

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    Uri ReleaseUri,
    bool UpdateAvailable)
{
    public bool ReleaseFound => LatestVersion is not null;
}

public sealed class GitHubUpdateChecker
{
    private static readonly Uri LatestReleaseApi =
        new("https://api.github.com/repos/primetime43/PkgLens/releases/latest");
    private static readonly Uri ReleasesPage =
        new("https://github.com/primetime43/PkgLens/releases");
    private static readonly HttpClient SharedClient = new();

    private readonly HttpClient _httpClient;

    public static GitHubUpdateChecker Default { get; } = new(SharedClient);

    public GitHubUpdateChecker(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        SemanticVersion current = SemanticVersion.Parse(currentVersion, "application version");
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("PkgLens-UpdateChecker/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(current.Display, null, ReleasesPage, false);
        response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        GitHubRelease? release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            content, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(release?.TagName))
            throw new InvalidDataException("GitHub's latest release did not include a tag name.");
        SemanticVersion latest = SemanticVersion.Parse(release.TagName, "latest GitHub release tag");
        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(releaseUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub's latest release did not include a valid release-page URL.");
        }

        return new UpdateCheckResult(current.Display, latest.Display, releaseUri, latest.CompareTo(current) > 0);
    }

    public static bool IsNewerVersion(string latestVersion, string currentVersion) =>
        SemanticVersion.Parse(latestVersion, "latest version")
            .CompareTo(SemanticVersion.Parse(currentVersion, "current version")) > 0;

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    private sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private readonly string[] _prerelease;

        private SemanticVersion(int major, int minor, int patch, string[] prerelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            _prerelease = prerelease;
        }

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string Display => $"{Major}.{Minor}.{Patch}" +
                                 (_prerelease.Length == 0 ? string.Empty : $"-{string.Join('.', _prerelease)}");

        public static SemanticVersion Parse(string value, string source)
        {
            string normalized = value.Trim();
            if (normalized.StartsWith('v') || normalized.StartsWith('V'))
                normalized = normalized[1..];
            int buildIndex = normalized.IndexOf('+');
            if (buildIndex >= 0)
                normalized = normalized[..buildIndex];
            string[] versionAndPrerelease = normalized.Split('-', 2);
            string[] core = versionAndPrerelease[0].Split('.');
            if (core.Length != 3 || !core.All(component =>
                    int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                throw new InvalidDataException($"The {source} '{value}' is not a semantic version.");
            }

            string[] prerelease = versionAndPrerelease.Length == 1
                ? Array.Empty<string>()
                : versionAndPrerelease[1].Split('.');
            if (prerelease.Any(identifier => identifier.Length == 0 ||
                    identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            {
                throw new InvalidDataException($"The {source} '{value}' has an invalid prerelease identifier.");
            }

            return new SemanticVersion(
                int.Parse(core[0], CultureInfo.InvariantCulture),
                int.Parse(core[1], CultureInfo.InvariantCulture),
                int.Parse(core[2], CultureInfo.InvariantCulture),
                prerelease);
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;
            int core = Major.CompareTo(other.Major);
            if (core == 0) core = Minor.CompareTo(other.Minor);
            if (core == 0) core = Patch.CompareTo(other.Patch);
            if (core != 0) return core;
            if (_prerelease.Length == 0) return other._prerelease.Length == 0 ? 0 : 1;
            if (other._prerelease.Length == 0) return -1;

            for (int index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
            {
                string left = _prerelease[index];
                string right = other._prerelease[index];
                bool leftNumeric = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out long leftNumber);
                bool rightNumeric = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out long rightNumber);
                int result = leftNumeric && rightNumeric
                    ? leftNumber.CompareTo(rightNumber)
                    : leftNumeric
                        ? -1
                        : rightNumeric
                            ? 1
                            : string.Compare(left, right, StringComparison.Ordinal);
                if (result != 0) return result;
            }
            return _prerelease.Length.CompareTo(other._prerelease.Length);
        }
    }
}
