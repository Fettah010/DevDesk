using System;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>Runs confirmed release mutations through the shared ActivityCenter.</summary>
public sealed class ReleaseOperationCoordinator
{
    private readonly ActivityCenter _activity;
    private readonly IReleaseNotesSaveService _notes;
    private readonly IReleaseVersionApplyService _version;
    private readonly IReleaseGitExecutionService _git;

    public ReleaseOperationCoordinator(
        ActivityCenter activity,
        IReleaseNotesSaveService notes,
        IReleaseVersionApplyService version,
        IReleaseGitExecutionService git)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    public OperationHandle SubmitLocalRelease(
        string projectName,
        ReleasePlan plan,
        bool confirmed,
        bool overwriteNotesConfirmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(plan);
        return _activity.Submit(new OperationRequest(
            OperationKind.Release,
            projectName,
            plan.RepositoryPath,
            $"Prepare release {plan.Tag}",
            $"Save notes, apply {plan.ProposedVersion}, commit and tag {plan.Tag}",
            (token, _) => ExecuteLocalReleaseAsync(plan, confirmed, overwriteNotesConfirmed, token),
            Configuration: plan.Channel,
            Platform: plan.Tag));
    }

    public OperationHandle SubmitTagPush(string projectName, ReleasePlan plan, bool confirmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(plan);
        return _activity.Submit(new OperationRequest(
            OperationKind.Release,
            projectName,
            plan.RepositoryPath,
            $"Push release tag {plan.Tag}",
            $"Push refs/tags/{plan.Tag} to {plan.RemoteName}",
            async (token, _) =>
            {
                if (!confirmed)
                {
                    return new OperationOutcome(
                        OperationStatus.Canceled,
                        Hint: "The release tag push was not confirmed.");
                }

                var result = await _git.PushTagConfirmedAsync(plan, confirmed: true, token)
                    .ConfigureAwait(false);
                if (result.Succeeded)
                {
                    return new OperationOutcome(
                        OperationStatus.Succeeded,
                        ExitCode: 0,
                        Hint: "The remote accepted the tag push. Workflow monitoring is shown on the GitHub page.");
                }

                var knownPreflightRefusal = result.Error.Contains(
                        "already exists on the remote",
                        StringComparison.OrdinalIgnoreCase)
                    || result.Error.Contains(
                        "selected push remote changed",
                        StringComparison.OrdinalIgnoreCase)
                    || result.Error.Contains(
                        "release tag does not exist locally",
                        StringComparison.OrdinalIgnoreCase)
                    || result.Error.Contains(
                        "invalid tag",
                        StringComparison.OrdinalIgnoreCase)
                    || result.Error.Contains(
                        "not configured",
                        StringComparison.OrdinalIgnoreCase);
                var detail = knownPreflightRefusal
                    ? result.Error
                    : $"The push did not report success. The remote outcome is unknown; verify whether {plan.Tag} exists remotely before retrying. {result.Error}";
                return new OperationOutcome(
                    result.Failure == GitFailureKind.Cancelled && knownPreflightRefusal
                        ? OperationStatus.Canceled
                        : OperationStatus.Failed,
                    result.ExitCode,
                    detail,
                    Hint: knownPreflightRefusal
                        ? "No release tag push was reported as accepted."
                        : "Remote outcome uncertain; inspect the remote tag before attempting any retry.");
            },
            Configuration: plan.Channel,
            Platform: plan.Tag));
    }

    private async Task<OperationOutcome> ExecuteLocalReleaseAsync(
        ReleasePlan plan,
        bool confirmed,
        bool overwriteNotesConfirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return OperationOutcome.Canceled();
        }

        var savedNotes = _notes.SaveConfirmed(
            plan.RepositoryPath,
            plan.ReleaseNotesPath,
            plan.ReleaseNotesMarkdown,
            confirmed: true,
            overwriteConfirmed: overwriteNotesConfirmed,
            cancellationToken);
        if (!savedNotes.Succeeded)
        {
            return OperationOutcome.Failed(null, savedNotes.Message, hint: "No version commit or tag was created.");
        }

        var applied = await _version.ApplyConfirmedAsync(
            plan.VersionPreview,
            confirmed: true,
            cancellationToken).ConfigureAwait(false);
        if (!applied.Succeeded)
        {
            return OperationOutcome.Failed(
                applied.ProcessResult?.ExitCode,
                $"Release notes were saved, but the project version was not applied: {applied.Message}",
                hint: "Review the release-notes file and project status; no release commit or tag was created.");
        }

        var committed = await _git.CommitVersionAndCreateTagConfirmedAsync(
            plan,
            confirmed: true,
            cancellationToken).ConfigureAwait(false);
        if (!committed.Succeeded || !committed.TagCreated)
        {
            return OperationOutcome.Failed(
                committed.Operation?.ExitCode,
                committed.Message,
                hint: committed.VersionCommitted
                    ? $"The version commit exists, but local tag {plan.Tag} may not. Inspect Git before continuing."
                    : "Review the saved notes and version file; no complete release commit/tag was confirmed.");
        }

        return new OperationOutcome(
            OperationStatus.Succeeded,
            ExitCode: 0,
            Hint: $"Local release commit and tag {plan.Tag} created. Push remains a separate confirmation.",
            OutputPath: committed.CommitHash);
    }
}
