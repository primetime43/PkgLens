using System.Net;
using System.Net.Http;
using System.Text;
using PkgLens.Gui.Services;

namespace PkgLens.Core.Tests.Gui;

public sealed class GitHubUpdateCheckerTests
{
    [Fact]
    public async Task CheckAsync_ReportsNewerLatestRelease()
    {
        var handler = new StubHandler("""
            { "tag_name": "v1.2.0", "html_url": "https://github.com/primetime43/PkgLens/releases/tag/v1.2.0" }
            """);
        var checker = new GitHubUpdateChecker(new HttpClient(handler));

        UpdateCheckResult result = await checker.CheckAsync("1.0.0");

        Assert.True(result.UpdateAvailable);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.Equal("api.github.com", handler.Request?.RequestUri?.Host);
        Assert.Contains("PkgLens-UpdateChecker", handler.Request?.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReportsWhenRepositoryHasNoPublishedRelease()
    {
        var checker = new GitHubUpdateChecker(new HttpClient(new StubHandler(string.Empty, HttpStatusCode.NotFound)));

        UpdateCheckResult result = await checker.CheckAsync("1.0.0");

        Assert.False(result.ReleaseFound);
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.1.0", false)]
    [InlineData("1.0.0", "1.0.0-beta.1", true)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", true)]
    [InlineData("v2.0.0+build.5", "1.9.9", true)]
    public void IsNewerVersion_UsesSemanticPrecedence(string latest, string current, bool expected) =>
        Assert.Equal(expected, GitHubUpdateChecker.IsNewerVersion(latest, current));

    private sealed class StubHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
