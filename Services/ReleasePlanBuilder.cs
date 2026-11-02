using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DevDesk.Models;

namespace DevDesk.Services;

public sealed record ReleasePlan(
    string RepositoryPath,
    GitHubRepositoryIdentity Repository,
    string ProjectFile,
    string RemoteName,
    string RemoteUrl,
    string ReleaseNotesPath,
    string ReleaseNotesMarkdown,
    string ReleaseNotesSha256,
    string CommitMessage,
    ReleaseVersionPreview VersionPreview)
{
    public string Tag => VersionPreview.Tag;
    public string Channel => VersionPreview.Channel;
    public string ProposedVersion => VersionPreview.ProposedVersion;
}

public static class ReleasePlanBuilder
{
    private static readonly UTF8Encoding NotesEncoding = new(false);

    public static bool TryCreate(
        string repositoryPath,
        GitHubRepositoryIdentity repository,
        GitRemoteInfo remote,
        ReleaseVersionPreview versionPreview,
        string releaseNotesPath,
        string releaseNotesMarkdown,
        string? commitMessage,
        out ReleasePlan? plan,
        out string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(versionPreview);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseNotesPath);
        ArgumentNullException.ThrowIfNull(releaseNotesMarkdown);
        plan = null;
        error = string.Empty;

        string root;
        string projectFile;
        string notesPath;
        try
        {
            root = Path.GetFullPath(repositoryPath);
            projectFile = Path.GetFullPath(versionPreview.ProjectFile);
            notesPath = Path.GetFullPath(releaseNotesPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "The selected repository, project, or release-notes path is invalid.";
            return false;
        }

        if (!repository.SupportsPublicApi
            || !string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            error = "Release plans require a repository supported by the public GitHub API.";
            return false;
        }

        if (!IsInsideRepository(root, projectFile)
            || !string.Equals(Path.GetExtension(notesPath), ".md", StringComparison.OrdinalIgnoreCase)
            || !IsInsideRepository(root, notesPath))
        {
            error = "The project and Markdown release-notes file must be inside the selected repository.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(remote.Name)
            || remote.Name.StartsWith("-", StringComparison.Ordinal)
            || !string.Equals(remote.Direction, "push", StringComparison.OrdinalIgnoreCase))
        {
            error = "Select a configured push remote for the release.";
            return false;
        }

        if (!IsSelectedPushRemote(remote, repository))
        {
            error = "The selected push remote does not point to the selected GitHub repository.";
            return false;
        }

        if (!ReleaseChannels.TryParseTagTyped(versionPreview.Tag, out var tagVersion, out var tagChannel)
            || tagVersion != versionPreview.ProposedVersion
            || !string.Equals(ReleaseChannels.ToName(tagChannel), versionPreview.Channel, StringComparison.Ordinal)
            || !string.Equals(
                versionPreview.Tag,
                tagChannel == ReleaseChannel.Stable
                    ? $"v{tagVersion}"
                    : $"v{tagVersion}-{ReleaseChannels.ToName(tagChannel)}",
                StringComparison.Ordinal))
        {
            error = "The release tag, version, and channel do not agree.";
            return false;
        }

        var channel = versionPreview.Channel switch
        {
            ReleaseChannels.Stable => ReleaseChannel.Stable,
            ReleaseChannels.Beta => ReleaseChannel.Beta,
            ReleaseChannels.Dev => ReleaseChannel.Dev,
            _ => (ReleaseChannel?)null
        };
        if (channel is null)
        {
            error = "The release preview has an unknown channel.";
            return false;
        }

        var expectedSuggestion = new ReleaseVersionSuggestion(
            versionPreview.BaselineVersion,
            versionPreview.ProposedVersion,
            ReleaseBumpKind.Patch,
            channel.Value,
            versionPreview.Tag);
        if (!ReleaseVersionPreviewBuilder.TryCreate(
                projectFile,
                expectedSuggestion,
                out var refreshedPreview,
                out var previewError)
            || refreshedPreview is null
            || refreshedPreview.CurrentVersion != versionPreview.CurrentVersion
            || !refreshedPreview.Changes.SequenceEqual(versionPreview.Changes))
        {
            error = $"The release version preview is stale or invalid: {previewError}";
            return false;
        }

        if (releaseNotesMarkdown.Contains('\0'))
        {
            error = "Release notes cannot contain null characters.";
            return false;
        }

        var message = string.IsNullOrWhiteSpace(commitMessage)
            ? $"Release {versionPreview.Tag}"
            : commitMessage.Trim();
        if (message.Length > 200 || message.Any(char.IsControl))
        {
            error = "The release commit subject must be a single line of at most 200 characters.";
            return false;
        }

        var notesHash = Convert.ToHexString(SHA256.HashData(NotesEncoding.GetBytes(releaseNotesMarkdown)));
        plan = new ReleasePlan(
            root,
            repository,
            projectFile,
            remote.Name,
            remote.Url,
            notesPath,
            releaseNotesMarkdown,
            notesHash,
            message,
            versionPreview);
        return true;
    }

    private static bool IsInsideRepository(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relativePath)
            && relativePath is not "." and not ".."
            && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static bool IsSelectedPushRemote(
        GitRemoteInfo remote,
        GitHubRepositoryIdentity repository)
    {
        if (!string.Equals(remote.Direction, "push", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parsed = GitHubRepositoryIdentityParser.Parse(remote.Url);
        return parsed is { SupportsPublicApi: true }
            && string.Equals(parsed.Owner, repository.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parsed.Repository, repository.Repository, StringComparison.OrdinalIgnoreCase);
    }
}
