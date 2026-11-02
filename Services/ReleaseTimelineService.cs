using System;
using System.Collections.Generic;
using System.Linq;
using DevDesk.Models;

namespace DevDesk.Services;

public sealed record ReleaseTimelineItem(
    string Name,
    string Version,
    string Channel,
    DateTimeOffset? PublishedAt,
    string Url);

public sealed record ReleaseTimeline(
    IReadOnlyList<ReleaseTimelineItem> Items,
    bool IsStale,
    DateTimeOffset? FetchedAt,
    string? Message)
{
    public static ReleaseTimeline FromResult(
        GitHubDataResult<GitHubRelease[]> result,
        GitHubService links)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(links);

        if (result.Data is null)
        {
            return new ReleaseTimeline(
                Array.Empty<ReleaseTimelineItem>(),
                result.IsStale,
                result.FetchedAt,
                result.Message ?? result.ErrorKind switch
                {
                    GitHubErrorKind.NoCredential => "Public release data is unavailable.",
                    GitHubErrorKind.Authentication => "GitHub authentication failed.",
                    GitHubErrorKind.Forbidden => "GitHub denied access to release data.",
                    GitHubErrorKind.RateLimited => "GitHub rate limit reached.",
                    GitHubErrorKind.Network or GitHubErrorKind.TimedOut => "GitHub is unavailable; try again later.",
                    GitHubErrorKind.NotFound => "No releases were found for this repository.",
                    _ => "Release data could not be loaded."
                });
        }

        var items = new List<ReleaseTimelineItem>();
        foreach (var release in result.Data)
        {
            if (release.Draft
                || release.PublishedAt is null
                || !TryParseCanonicalTag(release.TagName, out var version, out var channel)
                || !links.TryCreateWebUri(release.HtmlUrl, out var uri)
                || uri is null)
            {
                continue;
            }

            items.Add(new ReleaseTimelineItem(
                string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                version,
                channel,
                release.PublishedAt,
                uri.AbsoluteUri));
        }

        var ordered = items
            .OrderBy(item => ChannelOrder(item.Channel))
            .ThenByDescending(item => item.PublishedAt)
            .GroupBy(item => item.Channel, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return new ReleaseTimeline(
            ordered,
            result.IsStale,
            result.FetchedAt,
            ordered.Length == 0 ? "No published stable, beta, or dev releases are available." : null);
    }

    private static bool TryParseCanonicalTag(string tag, out string version, out string channel)
    {
        version = string.Empty;
        channel = string.Empty;
        if (!ReleaseChannels.TryParseTag(tag, out var parsedVersion, out var parsedChannel)
            || !ReleaseVersionSuggester.IsValidNumericVersion(parsedVersion))
        {
            return false;
        }

        var canonical = parsedChannel == ReleaseChannels.Stable
            ? $"v{parsedVersion}"
            : $"v{parsedVersion}-{parsedChannel}";
        if (!string.Equals(tag, canonical, StringComparison.Ordinal))
        {
            return false;
        }

        version = parsedVersion;
        channel = parsedChannel;
        return true;
    }

    private static int ChannelOrder(string channel) => channel switch
    {
        ReleaseChannels.Stable => 0,
        ReleaseChannels.Beta => 1,
        ReleaseChannels.Dev => 2,
        _ => 3
    };
}
