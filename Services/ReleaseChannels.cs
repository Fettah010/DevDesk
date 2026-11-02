using System;
using System.Collections.Generic;
using System.Globalization;

namespace DevDesk.Services;

public enum ReleaseChannel
{
    Stable,
    Beta,
    Dev
}

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

    public static string ToName(ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Stable => Stable,
        ReleaseChannel.Beta => Beta,
        ReleaseChannel.Dev => Dev,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown release channel.")
    };

    public static bool TryParseSuffix(string? suffix, out ReleaseChannel channel)
    {
        switch (suffix?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case Stable:
                channel = ReleaseChannel.Stable;
                return true;
            case Beta:
                channel = ReleaseChannel.Beta;
                return true;
            case Dev:
                channel = ReleaseChannel.Dev;
                return true;
            default:
                channel = ReleaseChannel.Stable;
                return false;
        }
    }

    /// <summary>
    /// Maps a tag suffix to a channel: <c>v0.1.0-beta</c> → <c>beta</c>,
    /// <c>v1.0.0</c> → <c>stable</c>. Unknown suffixes return null so callers
    /// can refuse instead of silently picking a feed.
    /// </summary>
    public static string? FromSuffix(string? suffix) =>
        TryParseSuffix(suffix, out var channel) ? ToName(channel) : null;

    /// <summary>
    /// Splits a release tag into its numeric version and channel.
    /// <c>v0.1.0-beta</c> → (<c>0.1.0</c>, <c>beta</c>).
    /// </summary>
    public static bool TryParseTag(string? tag, out string version, out string channel)
    {
        if (TryParseTagTyped(tag, out version, out var parsedChannel))
        {
            channel = ToName(parsedChannel);
            return true;
        }

        channel = Stable;
        return false;
    }

    public static bool TryParseTagTyped(string? tag, out string version, out ReleaseChannel channel)
    {
        version = string.Empty;
        channel = ReleaseChannel.Stable;

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

        if (!IsCanonicalNumericVersion(numeric))
        {
            return false;
        }

        if (!TryParseSuffix(suffix, out channel))
        {
            return false;
        }

        version = numeric;
        return true;
    }

    private static bool IsCanonicalNumericVersion(string value)
    {
        var components = value.Split('.');
        if (components.Length != 3)
        {
            return false;
        }

        foreach (var component in components)
        {
            if (component.Length == 0
                || (component.Length > 1 && component[0] == '0')
                || !int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            foreach (var character in component)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
