using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public sealed class ReleaseTimelineServiceTests
{
    [Fact]
    public void FromResult_UsesLatestCanonicalReleasePerChannelAndPreservesStaleState()
    {
        using var sandbox = new SettingsSandbox();
        var service = CreateLinksService(sandbox.Directory);
        var fetchedAt = DateTimeOffset.UtcNow;
        var releases = new[]
        {
            Release("v2.0.0", "Stable old", fetchedAt.AddDays(-4)),
            Release("v2.1.0", "Stable newest", fetchedAt.AddDays(-1)),
            Release("v2.2.0-beta", "Beta newest", fetchedAt),
            Release("v2.0.0-dev", "Dev newest", fetchedAt.AddHours(-2)),
            Release("v2.1.0-alpha", "Unsupported", fetchedAt),
            Release("V2.3.0-beta", "Non-canonical", fetchedAt),
            Release("v2.3.0-beta", "Unsafe link", fetchedAt, "https://example.com/owner/repo/releases/v2.3.0-beta")
        };
        var result = new GitHubDataResult<GitHubRelease[]>(
            releases,
            GitHubErrorKind.None,
            null,
            IsFromCache: true,
            IsStale: true,
            fetchedAt,
            null);

        var timeline = ReleaseTimeline.FromResult(result, service);

        Assert.True(timeline.IsStale);
        Assert.Equal(fetchedAt, timeline.FetchedAt);
        Assert.Equal(new[] { "stable", "beta", "dev" }, timeline.Items.Select(item => item.Channel));
        Assert.Equal(new[] { "Stable newest", "Beta newest", "Dev newest" }, timeline.Items.Select(item => item.Name));
        Assert.All(timeline.Items, item => Assert.StartsWith("https://github.com/", item.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void FromResult_ReportsSuccessfulEmptyResponseSeparatelyFromFailure()
    {
        using var sandbox = new SettingsSandbox();
        var service = CreateLinksService(sandbox.Directory);

        var empty = ReleaseTimeline.FromResult(
            Result(Array.Empty<GitHubRelease>()),
            service);
        var failed = ReleaseTimeline.FromResult(
            new GitHubDataResult<GitHubRelease[]>(
                null,
                GitHubErrorKind.Network,
                null,
                false,
                false,
                null,
                null),
            service);

        Assert.Empty(empty.Items);
        Assert.Contains("No published", empty.Message, StringComparison.Ordinal);
        Assert.Contains("unavailable", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GitHubService CreateLinksService(string directory) =>
        new(new GitHubApiClient(), new FakeCredentialStore(), new GitHubCacheStore(Path.Combine(directory, "cache.json")));

    private static GitHubDataResult<GitHubRelease[]> Result(GitHubRelease[]? data) =>
        new(data, GitHubErrorKind.None, null, false, false, DateTimeOffset.UtcNow, null);

    private static GitHubRelease Release(
        string tag,
        string name,
        DateTimeOffset publishedAt,
        string? url = null) =>
        new(
            tag,
            name,
            null,
            url ?? $"https://github.com/owner/repo/releases/tag/{tag}",
            false,
            tag.Contains('-', StringComparison.Ordinal),
            publishedAt,
            Array.Empty<GitHubReleaseAsset>());

    private sealed class FakeCredentialStore : IGitHubCredentialStore
    {
        public bool HasToken => false;
        public string? ReadToken() => null;
        public void SaveToken(string token) => throw new NotSupportedException();
        public void DeleteToken() => throw new NotSupportedException();
    }
}
