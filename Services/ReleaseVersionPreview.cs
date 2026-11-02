using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace DevDesk.Services;

public sealed record ReleaseVersionFieldChange(string Name, string CurrentValue, string ProposedValue);

public sealed record ReleaseVersionPreview(
    string ProjectFile,
    string CurrentVersion,
    string BaselineVersion,
    string ProposedVersion,
    string Channel,
    string Tag,
    IReadOnlyList<ReleaseVersionFieldChange> Changes)
{
    public bool HasChanges => Changes.Count > 0;

    public string Diff
    {
        get
        {
            var diff = new StringBuilder();
            foreach (var change in Changes)
            {
                diff.Append("- <").Append(change.Name).Append('>')
                    .Append(change.CurrentValue).Append("</").Append(change.Name).AppendLine(">");
                diff.Append("+ <").Append(change.Name).Append('>')
                    .Append(change.ProposedValue).Append("</").Append(change.Name).AppendLine(">");
            }

            return diff.ToString().TrimEnd();
        }
    }
}

public static class ReleaseVersionPreviewBuilder
{
    private static readonly string[] VersionProperties =
    [
        "Version",
        "AssemblyVersion",
        "FileVersion",
        "InformationalVersion"
    ];

    public static bool TryCreate(
        string projectFile,
        ReleaseVersionSuggestion suggestion,
        out ReleaseVersionPreview? preview,
        out string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);
        ArgumentNullException.ThrowIfNull(suggestion);
        preview = null;
        error = string.Empty;

        XDocument document;
        try
        {
            document = XDocument.Load(projectFile, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            error = "The project file could not be read as valid XML.";
            return false;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Project")
        {
            error = "The selected file is not an MSBuild project.";
            return false;
        }

        var properties = VersionProperties.ToDictionary(
            name => name,
            name => root.Descendants()
                .Where(element => element.Name.LocalName == name)
                .ToArray(),
            StringComparer.Ordinal);

        foreach (var property in properties)
        {
            if (property.Value.Length != 1)
            {
                error = $"Expected exactly one <{property.Key}> element in the project file.";
                return false;
            }
        }

        var values = properties.ToDictionary(
            property => property.Key,
            property => property.Value[0].Value.Trim(),
            StringComparer.Ordinal);

        var currentVersion = values["Version"];
        if (!string.Equals(values["AssemblyVersion"], $"{currentVersion}.0", StringComparison.Ordinal)
            || !string.Equals(values["FileVersion"], $"{currentVersion}.0", StringComparison.Ordinal)
            || !IsCurrentInformationalVersionValid(currentVersion, values["InformationalVersion"]))
        {
            error = "The project's four version fields are inconsistent. Correct the version mismatch before preparing a release.";
            return false;
        }

        var expectedChannel = ReleaseChannels.ToName(suggestion.Channel);
        var expectedTag = suggestion.Channel == ReleaseChannel.Stable
            ? $"v{suggestion.Version}"
            : $"v{suggestion.Version}-{expectedChannel}";
        if (!ReleaseChannels.TryParseTagTyped(suggestion.Tag, out var tagVersion, out var tagChannel)
            || !string.Equals(tagVersion, suggestion.Version, StringComparison.Ordinal)
            || tagChannel != suggestion.Channel
            || !string.Equals(suggestion.Tag, expectedTag, StringComparison.Ordinal)
            || !IsCanonicalNumericVersion(suggestion.Version)
            || !IsCanonicalNumericVersion(suggestion.BaselineVersion))
        {
            error = "The proposed release tag does not match the selected version and channel.";
            return false;
        }

        if (!ReleaseVersionSuggester.TryCompareNumericVersions(
                suggestion.Version,
                currentVersion,
                out var proposedVersionComparison)
            || proposedVersionComparison <= 0)
        {
            error = "The proposed release version must be greater than the project's current numeric version.";
            return false;
        }

        var proposed = new[]
        {
            suggestion.Version,
            $"{suggestion.Version}.0",
            $"{suggestion.Version}.0",
            suggestion.Channel == ReleaseChannel.Stable
                ? suggestion.Version
                : $"{suggestion.Version}-{expectedChannel}"
        };

        var changes = VersionProperties
            .Select((name, index) => new ReleaseVersionFieldChange(name, values[name], proposed[index]))
            .Where(change => !string.Equals(change.CurrentValue, change.ProposedValue, StringComparison.Ordinal))
            .ToArray();

        preview = new ReleaseVersionPreview(
            Path.GetFullPath(projectFile),
            currentVersion,
            suggestion.BaselineVersion,
            suggestion.Version,
            expectedChannel,
            suggestion.Tag,
            changes);
        return true;
    }

    public static bool TryVerifyApplied(
        string projectFile,
        string version,
        string channel,
        out string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        error = string.Empty;

        XDocument document;
        try
        {
            document = XDocument.Load(projectFile, LoadOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            error = "The project file could not be read as valid XML.";
            return false;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Project")
        {
            error = "The selected file is not an MSBuild project.";
            return false;
        }

        var properties = VersionProperties.ToDictionary(
            name => name,
            name => root.Descendants()
                .Where(element => element.Name.LocalName == name)
                .ToArray(),
            StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property.Value.Length != 1)
            {
                error = $"Expected exactly one <{property.Key}> element in the project file.";
                return false;
            }
        }

        var channelName = channel switch
        {
            ReleaseChannels.Stable => ReleaseChannels.Stable,
            ReleaseChannels.Beta => ReleaseChannels.Beta,
            ReleaseChannels.Dev => ReleaseChannels.Dev,
            _ => null
        };
        if (channelName is null || !IsCanonicalNumericVersion(version))
        {
            error = "The confirmed release version or channel is invalid.";
            return false;
        }

        var expectedInformationalVersion = channelName == ReleaseChannels.Stable
            ? version
            : $"{version}-{channelName}";
        var expectedValues = new[]
        {
            version,
            $"{version}.0",
            $"{version}.0",
            expectedInformationalVersion
        };

        for (var index = 0; index < VersionProperties.Length; index++)
        {
            var actual = properties[VersionProperties[index]][0].Value.Trim();
            if (!string.Equals(actual, expectedValues[index], StringComparison.Ordinal))
            {
                error = $"The applied <{VersionProperties[index]}> value does not match the confirmed release version.";
                return false;
            }
        }

        return true;
    }

    private static bool IsCurrentInformationalVersionValid(string numericVersion, string informationalVersion)
    {
        if (!ReleaseChannels.TryParseTagTyped($"v{informationalVersion}", out var parsedVersion, out _))
        {
            return false;
        }

        return string.Equals(parsedVersion, numericVersion, StringComparison.Ordinal)
            && IsCanonicalNumericVersion(numericVersion);
    }

    private static bool IsCanonicalNumericVersion(string version) =>
        ReleaseVersionSuggester.IsValidNumericVersion(version);
}
