using System;
using System.Collections.Generic;

namespace DevDesk.Services;

/// <summary>
/// The release-channel vocabulary, in exactly one place (roadmap D4): CI,
/// settings, the updater, the tag parser and the UI all read it from here.
/// Velopack keeps one version per channel, so a new tag must map to exactly one.
/// </summary>
public static class ReleaseChannels
{
    public const string Stable = "stable";
    public const string Beta = "beta";
    public const string Dev = "dev";

    /// <summary>Before 1.0 every build is a beta (roadmap D5).</summary>
    public const string Default = Beta;

    public static IReadOnlyList<string> All { get; } = new[] { Stable, Beta, Dev };

    public static bool IsKnown(string? channel) =>
        channel is Stable or Beta or Dev;

    /// <summary>
    /// Maps a tag suffix to a channel: <c>v0.1.0-beta</c> → <c>beta</c>,
    /// <c>v1.0.0</c> → <c>stable</c>. Unknown suffixes return null so callers
    /// can refuse instead of silently picking a feed.
    /// </summary>
    public static string? FromSuffix(string? suffix) => suffix?.Trim().ToLowerInvariant() switch
    {
        null or "" or "stable" => Stable,
        "beta" => Beta,
        "dev" => Dev,
        _ => null
    };

    /// <summary>
    /// Splits a release tag into its numeric version and channel.
    /// <c>v0.1.0-beta</c> → (<c>0.1.0</c>, <c>beta</c>).
    /// </summary>
    public static bool TryParseTag(string? tag, out string version, out string channel)
    {
        version = string.Empty;
        channel = Stable;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var dash = value.IndexOf('-');
        var numeric = dash >= 0 ? value[..dash] : value;
        var suffix = dash >= 0 ? value[(dash + 1)..] : string.Empty;

        if (!Version.TryParse(numeric, out var parsed) || parsed.Build < 0)
        {
            return false;
        }

        var resolved = FromSuffix(suffix);
        if (resolved is null)
        {
            return false;
        }

        version = $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
        channel = resolved;
        return true;
    }
}
