using System.Collections.Generic;
using System.Globalization;

namespace DevDesk.Services;

public enum ReleaseBumpKind
{
    Patch,
    Minor,
    Major
}

public sealed record ReleaseVersionSuggestion(
    string BaselineVersion,
    string Version,
    ReleaseBumpKind BumpKind,
    ReleaseChannel Channel,
    string Tag);

public static class ReleaseVersionSuggester
{
    public static bool IsValidNumericVersion(string? version) =>
        TryParseNumericVersion(version, out _, out _, out _);

    public static bool TryCompareNumericVersions(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParseNumericVersion(left, out var leftMajor, out var leftMinor, out var leftPatch)
            || !TryParseNumericVersion(right, out var rightMajor, out var rightMinor, out var rightPatch))
        {
            return false;
        }

        comparison = Compare(leftMajor, leftMinor, leftPatch, rightMajor, rightMinor, rightPatch);
        return true;
    }

    public static bool TrySuggestFromTags(
        IEnumerable<string> tags,
        string? currentProjectVersion,
        ReleaseBumpKind bumpKind,
        ReleaseChannel channel,
        out ReleaseVersionSuggestion? suggestion,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(tags);

        string? baseline = null;
        var hasChannelTag = false;
        var highestMajor = 0;
        var highestMinor = 0;
        var highestPatch = 0;

        foreach (var tag in tags)
        {
            if (!TryParseReleaseTag(tag, out var version, out var tagChannel)
                || tagChannel != channel
                || !TryParseNumericVersion(version, out var major, out var minor, out var patch))
            {
                continue;
            }

            if (!hasChannelTag || Compare(major, minor, patch, highestMajor, highestMinor, highestPatch) > 0)
            {
                hasChannelTag = true;
                highestMajor = major;
                highestMinor = minor;
                highestPatch = patch;
                baseline = version;
            }
        }

        baseline ??= currentProjectVersion;
        return TrySuggest(baseline, bumpKind, channel, out suggestion, out error);
    }

    private static bool TryParseReleaseTag(
        string? tag,
        out string version,
        out ReleaseChannel channel)
    {
        version = string.Empty;
        channel = ReleaseChannel.Stable;

        if (!ReleaseChannels.TryParseTagTyped(tag, out var parsedVersion, out var parsedChannel))
        {
            return false;
        }

        var value = tag!.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var dash = value.IndexOf('-');
        var numeric = dash >= 0 ? value[..dash] : value;
        if (!TryParseNumericVersion(numeric, out _, out _, out _)
            || !string.Equals(numeric, parsedVersion, StringComparison.Ordinal)
            || (dash >= 0 && parsedChannel == ReleaseChannel.Stable))
        {
            return false;
        }

        version = numeric;
        channel = parsedChannel;
        return true;
    }

    public static bool TrySuggestNextPatch(
        string? baselineVersion,
        ReleaseChannel channel,
        out ReleaseVersionSuggestion? suggestion,
        out string error) =>
        TrySuggest(baselineVersion, ReleaseBumpKind.Patch, channel, out suggestion, out error);

    public static bool TrySuggest(
        string? baselineVersion,
        ReleaseBumpKind bumpKind,
        ReleaseChannel channel,
        out ReleaseVersionSuggestion? suggestion,
        out string error)
    {
        suggestion = null;
        error = string.Empty;

        if (!TryParseNumericVersion(baselineVersion, out var major, out var minor, out var patch))
        {
            error = "Version must be three non-negative integers without leading zeroes (MAJOR.MINOR.PATCH).";
            return false;
        }

        if (!Enum.IsDefined(bumpKind))
        {
            error = "Unknown version bump.";
            return false;
        }

        if (!Enum.IsDefined(channel))
        {
            error = "Unknown release channel.";
            return false;
        }

        try
        {
            switch (bumpKind)
            {
                case ReleaseBumpKind.Patch:
                    patch = checked(patch + 1);
                    break;
                case ReleaseBumpKind.Minor:
                    minor = checked(minor + 1);
                    patch = 0;
                    break;
                case ReleaseBumpKind.Major:
                    major = checked(major + 1);
                    minor = 0;
                    patch = 0;
                    break;
            }
        }
        catch (OverflowException)
        {
            error = "The selected version component cannot be incremented further.";
            return false;
        }

        var version = string.Join('.',
            major.ToString(CultureInfo.InvariantCulture),
            minor.ToString(CultureInfo.InvariantCulture),
            patch.ToString(CultureInfo.InvariantCulture));
        var channelName = ReleaseChannels.ToName(channel);
        var tag = channel == ReleaseChannel.Stable
            ? $"v{version}"
            : $"v{version}-{channelName}";

        suggestion = new ReleaseVersionSuggestion(baselineVersion!, version, bumpKind, channel, tag);
        return true;
    }

    private static bool TryParseNumericVersion(
        string? value,
        out int major,
        out int minor,
        out int patch)
    {
        major = minor = patch = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split('.');
        return parts.Length == 3
            && TryParseComponent(parts[0], out major)
            && TryParseComponent(parts[1], out minor)
            && TryParseComponent(parts[2], out patch);
    }

    private static int Compare(int major, int minor, int patch, int otherMajor, int otherMinor, int otherPatch)
    {
        var majorComparison = major.CompareTo(otherMajor);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = minor.CompareTo(otherMinor);
        return minorComparison != 0 ? minorComparison : patch.CompareTo(otherPatch);
    }

    private static bool TryParseComponent(string value, out int component)
    {
        component = 0;
        return value.Length > 0
            && (value.Length == 1 || value[0] != '0')
            && value.All(char.IsAsciiDigit)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out component);
    }
}
