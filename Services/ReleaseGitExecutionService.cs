using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

public sealed record ReleaseGitExecutionResult(
    bool Succeeded,
    string Message,
    bool VersionFileStaged,
    bool VersionCommitted,
    bool TagCreated,
    string? CommitHash = null,
    GitOperationResult? Operation = null);

public interface IReleaseGitExecutionService
{
    Task<ReleaseGitExecutionResult> CommitVersionAndCreateTagConfirmedAsync(
        ReleasePlan plan,
        bool confirmed,
        CancellationToken cancellationToken = default);

    Task<GitOperationResult> PushTagConfirmedAsync(
        ReleasePlan plan,
        bool confirmed,
        CancellationToken cancellationToken = default);
}

public sealed class ReleaseGitExecutionService : IReleaseGitExecutionService
{
    private readonly GitService _git;

    public ReleaseGitExecutionService(GitService git) =>
        _git = git ?? throw new ArgumentNullException(nameof(git));

    public async Task<ReleaseGitExecutionResult> CommitVersionAndCreateTagConfirmedAsync(
        string repositoryPath,
        ReleaseVersionPreview preview,
        string commitMessage,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        return await CommitVersionAndCreateTagCoreAsync(
            repositoryPath,
            preview,
            commitMessage,
            null,
            null,
            null,
            confirmed,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ReleaseGitExecutionResult> CommitVersionAndCreateTagConfirmedAsync(
        ReleasePlan plan,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return CommitVersionAndCreateTagCoreAsync(
            plan.RepositoryPath,
            plan.VersionPreview,
            plan.CommitMessage,
            plan.ReleaseNotesPath,
            plan.ReleaseNotesSha256,
            plan.ReleaseNotesMarkdown,
            confirmed,
            cancellationToken);
    }

    private async Task<ReleaseGitExecutionResult> CommitVersionAndCreateTagCoreAsync(
        string repositoryPath,
        ReleaseVersionPreview preview,
        string commitMessage,
        string? releaseNotesPath,
        string? releaseNotesSha256,
        string? releaseNotesContent,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(preview);

        if (!confirmed)
        {
            return Failure("The local version commit and tag were not confirmed.");
        }

        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            return Failure("A release commit message is required.");
        }

        string repositoryRoot;
        string projectFile;
        string relativeProjectFile;
        try
        {
            repositoryRoot = Path.GetFullPath(repositoryPath);
            projectFile = Path.GetFullPath(preview.ProjectFile);
            relativeProjectFile = Path.GetRelativePath(repositoryRoot, projectFile);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Failure("The repository or project file path is invalid.");
        }

        if (!File.Exists(projectFile)
            || Path.IsPathRooted(relativeProjectFile)
            || relativeProjectFile == "."
            || relativeProjectFile == ".."
            || relativeProjectFile.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativeProjectFile.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Failure("The version project file must be inside the selected repository.");
        }

        string? notesPath = null;
        string? relativeNotesPath = null;
        if (releaseNotesPath is not null || releaseNotesSha256 is not null || releaseNotesContent is not null)
        {
            if (releaseNotesPath is null || releaseNotesSha256 is null || releaseNotesContent is null)
            {
                return Failure("The release plan contains an incomplete notes-file preview.");
            }

            try
            {
                notesPath = Path.GetFullPath(releaseNotesPath);
                relativeNotesPath = Path.GetRelativePath(repositoryRoot, notesPath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Failure("The release-notes path is invalid.");
            }

            if (!File.Exists(notesPath)
                || Path.IsPathRooted(relativeNotesPath!)
                || relativeNotesPath is "." or ".."
                || relativeNotesPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relativeNotesPath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || string.Equals(notesPath, projectFile, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(notesPath), ".md", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("The release-notes Markdown file must be inside the repository and separate from the project file.");
            }

            var contentHash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(releaseNotesContent)));
            if (!string.Equals(contentHash, releaseNotesSha256, StringComparison.OrdinalIgnoreCase)
                || !TryGetFileSha256(notesPath, out var savedNotesHash)
                || !string.Equals(savedNotesHash, releaseNotesSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("The saved release notes changed after the release plan was confirmed.");
            }
        }

        if (!ReleaseChannels.TryParseTagTyped(preview.Tag, out var tagVersion, out var tagChannel)
            || tagVersion != preview.ProposedVersion
            || !string.Equals(ReleaseChannels.ToName(tagChannel), preview.Channel, StringComparison.Ordinal)
            || !string.Equals(
                preview.Tag,
                tagChannel == ReleaseChannel.Stable
                    ? $"v{tagVersion}"
                    : $"v{tagVersion}-{ReleaseChannels.ToName(tagChannel)}",
                StringComparison.Ordinal))
        {
            return Failure("The release tag does not match the previewed version and channel.");
        }

        if (!ReleaseVersionPreviewBuilder.TryVerifyApplied(
                projectFile,
                preview.ProposedVersion,
                preview.Channel,
                out var versionError))
        {
            return Failure($"The project version fields do not match the confirmed preview: {versionError}");
        }

        var status = await _git.GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Operation.Succeeded || status.Value is null)
        {
            return Failure("Could not verify the repository status before preparing the release.", status.Operation);
        }

        if (status.Value.IsBareRepository)
        {
            return Failure("A release commit requires a working tree.", status.Operation);
        }

        var expectedPath = relativeProjectFile.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var expectedNotesGitPath = relativeNotesPath?
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var projectChange = status.Value.Files.SingleOrDefault(file =>
            string.Equals(file.Path, expectedPath, StringComparison.Ordinal));
        var notesChange = expectedNotesGitPath is null
            ? null
            : status.Value.Files.SingleOrDefault(file =>
                string.Equals(file.Path, expectedNotesGitPath, StringComparison.Ordinal));
        if (projectChange is null
            || projectChange.IndexStatus != "."
            || projectChange.WorktreeStatus == "."
            || status.Value.Files.Any(file =>
                !string.Equals(file.Path, expectedPath, StringComparison.Ordinal)
                && !string.Equals(file.Path, expectedNotesGitPath, StringComparison.Ordinal))
            || notesChange is not null
                && !((notesChange.IndexStatus == "." && notesChange.WorktreeStatus != ".")
                    || (notesChange.IndexStatus == "?" && notesChange.WorktreeStatus == "?")))
        {
            return Failure(
                "Only the previewed, unstaged version file and optional release-notes file may be present. Review or commit other changes first.",
                status.Operation);
        }

        var tags = await _git.GetTagsAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!tags.Operation.Succeeded || tags.Value is null)
        {
            return Failure("Could not verify local tags before preparing the release.", tags.Operation);
        }

        if (tags.Value.Any(tag => string.Equals(tag.Name, preview.Tag, StringComparison.Ordinal)))
        {
            return Failure("That release tag already exists locally. DevDesk will not move or replace it.");
        }

        if (notesPath is not null
            && (!TryGetFileSha256(notesPath, out var latestNotesHash)
                || !string.Equals(latestNotesHash, releaseNotesSha256, StringComparison.OrdinalIgnoreCase)))
        {
            return Failure("The release-notes file changed before staging; review the release plan again.");
        }

        var pathsToStage = notesChange is null
            ? new[] { expectedPath }
            : new[] { expectedPath, expectedNotesGitPath! };
        var staged = await _git.StageAsync(
            repositoryRoot,
            pathsToStage,
            cancellationToken).ConfigureAwait(false);
        if (!staged.Succeeded)
        {
            return Failure("Could not stage the previewed release files.", staged);
        }

        if (notesPath is not null
            && (!TryGetFileSha256(notesPath, out var stagedNotesHash)
                || !string.Equals(stagedNotesHash, releaseNotesSha256, StringComparison.OrdinalIgnoreCase)))
        {
            return new ReleaseGitExecutionResult(
                false,
                "The release-notes file changed while staging. The selected files remain staged for review; no release commit was made.",
                true,
                false,
                false);
        }

        var committed = await _git.CommitAsync(
            repositoryRoot,
            commitMessage,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!committed.Succeeded)
        {
            return new ReleaseGitExecutionResult(
                false,
                "The release files were staged, but the release commit failed. The staged changes were left intact.",
                true,
                false,
                false,
                Operation: committed);
        }

        var commit = await _git.RunAsync(
            repositoryRoot,
            cancellationToken,
            "rev-parse",
            "--verify",
            "HEAD").ConfigureAwait(false);
        var commitHash = commit.Succeeded ? commit.Output.Trim() : null;

        var createdTag = await _git.CreateTagAsync(
            repositoryRoot,
            preview.Tag,
            $"Release {preview.Tag}",
            cancellationToken).ConfigureAwait(false);
        if (!createdTag.Succeeded)
        {
            return new ReleaseGitExecutionResult(
                false,
                "The release commit was created, but the tag could not be created. The commit was not undone.",
                true,
                true,
                false,
                commitHash,
                createdTag);
        }

        return new ReleaseGitExecutionResult(
            true,
            "The release version was committed and its local tag was created. Pushing remains a separate confirmed action.",
            true,
            true,
            true,
            commitHash);
    }

    public async Task<GitOperationResult> PushTagConfirmedAsync(
        string repositoryPath,
        string remoteName,
        string tag,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            return new GitOperationResult(
                false,
                GitFailureKind.Cancelled,
                -1,
                string.Empty,
                "Pushing the release tag was not confirmed.");
        }

        return await _git.PushTagAsync(repositoryPath, remoteName, tag, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GitOperationResult> PushTagConfirmedAsync(
        ReleasePlan plan,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!confirmed)
        {
            return new GitOperationResult(
                false,
                GitFailureKind.Cancelled,
                -1,
                string.Empty,
                "Pushing the release tag was not confirmed.");
        }

        if (!ReleaseChannels.TryParseTagTyped(plan.Tag, out var version, out var channel)
            || version != plan.ProposedVersion
            || !string.Equals(
                plan.Tag,
                channel == ReleaseChannel.Stable
                    ? $"v{version}"
                    : $"v{version}-{ReleaseChannels.ToName(channel)}",
                StringComparison.Ordinal))
        {
            return InvalidPlanPush("The release plan contains an invalid tag.");
        }

        var remotes = await _git.GetRemotesAsync(plan.RepositoryPath, cancellationToken).ConfigureAwait(false);
        if (!remotes.Operation.Succeeded || remotes.Value is null)
        {
            return remotes.Operation;
        }

        var remote = remotes.Value.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, plan.RemoteName, StringComparison.Ordinal));
        if (remote is null || !ReleasePlanBuilder.IsSelectedPushRemote(remote, plan.Repository))
        {
            return InvalidPlanPush(
                "The selected push remote changed after the release plan was created. Review the plan before pushing.");
        }

        return await _git.PushTagAsync(
            plan.RepositoryPath,
            plan.RemoteName,
            plan.Tag,
            cancellationToken).ConfigureAwait(false);
    }

    private static ReleaseGitExecutionResult Failure(
        string message,
        GitOperationResult? operation = null) =>
        new(false, message, false, false, false, Operation: operation);

    private static GitOperationResult InvalidPlanPush(string message) =>
        new(false, GitFailureKind.Other, -1, string.Empty, message);

    private static bool TryGetFileSha256(string path, out string hash)
    {
        try
        {
            hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            hash = string.Empty;
            return false;
        }
    }
}
