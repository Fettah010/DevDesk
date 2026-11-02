using System.Security.Cryptography;
using System.Text;
using DevDesk.Models;
using DevDesk.Services;
using DevDesk.ViewModels;
using Xunit;

namespace DevDesk.Tests;

public sealed class ReleaseOperationCoordinatorTests
{
    [Fact]
    public async Task SubmitTagPush_RecordsUncertainRemoteOutcomeAndCannotBeRetried()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        var center = new ActivityCenter(history: history);
        var git = new FakeReleaseGit
        {
            PushResult = new GitOperationResult(
                false,
                GitFailureKind.Cancelled,
                -1,
                string.Empty,
                "Git operation cancelled.")
        };
        var coordinator = new ReleaseOperationCoordinator(
            center,
            new FakeNotes(),
            new FakeVersion(),
            git);

        var operation = await coordinator.SubmitTagPush("DevDesk", CreatePlan(), confirmed: true)
            .Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationKind.Release, operation.Kind);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains("outcome is unknown", operation.FirstError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verify whether v0.6.0-beta exists remotely", operation.FirstError, StringComparison.Ordinal);
        Assert.Contains("inspect the remote tag", operation.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Single(history.Entries);
        Assert.Equal(OperationKind.Release, history.Entries[0].Kind);
        Assert.Null(history.LastFor(CreatePlan().RepositoryPath));

        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();
        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("Release", row.KindLabel);
        Assert.False(row.CanRetry);
        Assert.Null(viewModel.Retry(operation.Id));
    }

    [Fact]
    public async Task SubmitTagPush_WhenDeclined_DoesNotCallGit()
    {
        using var sandbox = new SettingsSandbox();
        var center = new ActivityCenter(history: new BuildHistory(sandbox.Directory));
        var git = new FakeReleaseGit();
        var coordinator = new ReleaseOperationCoordinator(
            center,
            new FakeNotes(),
            new FakeVersion(),
            git);

        var operation = await coordinator.SubmitTagPush("DevDesk", CreatePlan(), confirmed: false)
            .Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Canceled, operation.Status);
        Assert.False(git.PushCalled);
    }

    [Fact]
    public async Task SubmitLocalRelease_RunsConfirmedStepsInOrderAndPersistsOneActivityRecord()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        var center = new ActivityCenter(history: history);
        var notes = new FakeNotes();
        var version = new FakeVersion();
        var git = new FakeReleaseGit
        {
            CommitResult = new ReleaseGitExecutionResult(true, "created", true, true, true, "abc123")
        };
        var coordinator = new ReleaseOperationCoordinator(center, notes, version, git);

        var operation = await coordinator.SubmitLocalRelease("DevDesk", CreatePlan(), true, false)
            .Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal(new[] { "save", "apply", "commit" }, notes.Steps.Concat(version.Steps).Concat(git.Steps));
        Assert.Single(history.Entries);
        Assert.Equal("beta", history.Entries[0].Configuration);
        Assert.Equal("v0.6.0-beta", history.Entries[0].Platform);
    }

    [Fact]
    public async Task SubmitLocalRelease_WhenNotesSaveFails_DoesNotApplyOrCommit()
    {
        using var sandbox = new SettingsSandbox();
        var center = new ActivityCenter(history: new BuildHistory(sandbox.Directory));
        var version = new FakeVersion();
        var git = new FakeReleaseGit();
        var coordinator = new ReleaseOperationCoordinator(
            center,
            new FakeNotes { Result = new ReleaseNotesSaveResult(false, "write failed") },
            version,
            git);

        var operation = await coordinator.SubmitLocalRelease("DevDesk", CreatePlan(), true, false)
            .Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains("write failed", operation.FirstError);
        Assert.Empty(version.Steps);
        Assert.Empty(git.Steps);
    }

    private static ReleasePlan CreatePlan()
    {
        var preview = new ReleaseVersionPreview(
            @"C:\repo\DevDesk.csproj",
            "0.5.0",
            "0.5.0",
            "0.6.0",
            "beta",
            "v0.6.0-beta",
            Array.Empty<ReleaseVersionFieldChange>());
        var notes = "# Release";
        return new ReleasePlan(
            @"C:\repo",
            new GitHubRepositoryIdentity("github.com", "owner", "repo"),
            preview.ProjectFile,
            "origin",
            "https://github.com/owner/repo.git",
            @"C:\repo\ReleaseNotes.md",
            notes,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notes))),
            "Release v0.6.0-beta",
            preview);
    }

    private sealed class FakeNotes : IReleaseNotesSaveService
    {
        public ReleaseNotesSaveResult Result { get; init; } = new(true, "saved");
        public List<string> Steps { get; } = new();

        public ReleaseNotesSaveResult SaveConfirmed(
            string repositoryPath,
            string targetPath,
            string content,
            bool confirmed,
            bool overwriteConfirmed,
            CancellationToken cancellationToken = default)
        {
            Steps.Add("save");
            return Result;
        }
    }

    private sealed class FakeVersion : IReleaseVersionApplyService
    {
        public List<string> Steps { get; } = new();
        public Task<ReleaseVersionApplyResult> ApplyConfirmedAsync(
            ReleaseVersionPreview preview,
            bool confirmed,
            CancellationToken cancellationToken = default)
        {
            Steps.Add("apply");
            return Task.FromResult(new ReleaseVersionApplyResult(true, "applied"));
        }
    }

    private sealed class FakeReleaseGit : IReleaseGitExecutionService
    {
        public bool PushCalled { get; private set; }
        public List<string> Steps { get; } = new();
        public ReleaseGitExecutionResult CommitResult { get; init; } =
            new(true, "created", true, true, true, "abc123");
        public GitOperationResult PushResult { get; init; } =
            new(true, GitFailureKind.None, 0, string.Empty, string.Empty);

        public Task<ReleaseGitExecutionResult> CommitVersionAndCreateTagConfirmedAsync(
            ReleasePlan plan,
            bool confirmed,
            CancellationToken cancellationToken = default)
        {
            Steps.Add("commit");
            return Task.FromResult(CommitResult);
        }

        public Task<GitOperationResult> PushTagConfirmedAsync(
            ReleasePlan plan,
            bool confirmed,
            CancellationToken cancellationToken = default)
        {
            PushCalled = true;
            Steps.Add("push");
            return Task.FromResult(PushResult);
        }
    }
}
