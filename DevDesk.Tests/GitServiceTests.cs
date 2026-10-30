using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class GitServiceTests
{
    [Theory]
    [InlineData("fatal: Authentication failed", GitFailureKind.Authentication)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", GitFailureKind.Authentication)]
    [InlineData("fatal: could not resolve host: example.invalid", GitFailureKind.Network)]
    [InlineData("CONFLICT (content): Merge conflict in file.txt", GitFailureKind.Conflict)]
    [InlineData("fatal: Unable to create '.git/index.lock': File exists", GitFailureKind.IndexLocked)]
    [InlineData("git timed out.", GitFailureKind.TimedOut)]
    public void Classify_MapsKnownFailures(string error, GitFailureKind expected)
    {
        Assert.Equal(expected, GitService.Classify(error));
    }

    [Fact]
    public void Redact_RemovesCredentialsFromRemoteUrl()
    {
        var redacted = GitService.Redact("fatal: https://user:secret@example.com/repo.git denied");

        Assert.Equal("fatal: https://[redacted]@example.com/repo.git denied", redacted);
        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsTypedSuccess()
    {
        using var sandbox = new GitSandbox();
        var service = new GitService();

        var result = await service.RunAsync(sandbox.Root, CancellationToken.None, "--version");

        Assert.True(result.Succeeded);
        Assert.Equal(GitFailureKind.None, result.Failure);
        Assert.Contains("git version", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_StatusAndHistoryFromScratchRepository()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("ReadRepo");
        await sandbox.CommitAsync(repo, "readme.txt");
        var service = new GitService();

        var status = await service.GetStatusAsync(repo);
        var history = await service.GetHistoryAsync(repo);

        Assert.True(status.Operation.Succeeded);
        Assert.NotNull(status.Value);
        Assert.Single(history.Value!);
        Assert.Equal("readme.txt", history.Value![0].Subject);
    }

    [Fact]
    public async Task StageCommitAndDiscard_UseRepositoryRelativePaths()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("WriteRepo");
        await sandbox.WriteAndCommitAsync(repo, ("file with spaces.txt", "before"));
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "file with spaces.txt"), "after");
        var service = new GitService();

        var stage = await service.StageAsync(repo, new[] { "file with spaces.txt" });
        var status = await service.GetStatusAsync(repo);
        var commit = await service.CommitAsync(repo, "update file");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "file with spaces.txt"), "discard me");
        var fileDiff = await service.GetFileDiffAsync(repo, "file with spaces.txt");
        var diffstat = await service.GetCommitDiffstatAsync(repo, (await service.GetHistoryAsync(repo)).Value![0].Hash);
        var discard = await service.DiscardTrackedChangesAsync(repo, new[] { "file with spaces.txt" });

        Assert.True(stage.Succeeded, stage.Error);
        Assert.Equal("M", Assert.Single(status.Value!.Files).IndexStatus);
        Assert.True(commit.Succeeded, commit.Error);
        Assert.Contains("discard me", fileDiff.Output, StringComparison.Ordinal);
        Assert.Contains("file with spaces.txt", diffstat.Output, StringComparison.Ordinal);
        Assert.True(discard.Succeeded, discard.Error);
        Assert.Equal("after", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(repo, "file with spaces.txt")));
    }

    [Fact]
    public async Task StageAllAndUnstage_KeepWorktreeFilesInPlace()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("StageRepo");
        await sandbox.WriteAndCommitAsync(repo, ("tracked.txt", "before"));
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "tracked.txt"), "after");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "new.txt"), "new");
        var service = new GitService();

        var stageAll = await service.StageAllAsync(repo);
        var staged = await service.GetStatusAsync(repo);
        var unstage = await service.UnstageAsync(repo, new[] { "tracked.txt" });
        var afterUnstage = await service.GetStatusAsync(repo);

        Assert.True(stageAll.Succeeded, stageAll.Error);
        Assert.Equal(2, staged.Value!.Files.Count);
        Assert.True(unstage.Succeeded, unstage.Error);
        Assert.Equal(".", afterUnstage.Value!.Files.Single(file => file.Path == "tracked.txt").IndexStatus);
        Assert.Equal("after", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(repo, "tracked.txt")));
    }

    [Fact]
    public async Task BranchAndTagOperations_ValidateThenChangeOnlyLocalRefs()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("RefRepo");
        await sandbox.CommitAsync(repo, "base.txt");
        var service = new GitService();
        var original = (await service.RunAsync(repo, CancellationToken.None, "symbolic-ref", "--short", "HEAD")).Output.Trim();

        var create = await service.CreateBranchAsync(repo, "feature/safe-git");
        var switchBack = await service.SwitchBranchAsync(repo, original);
        var deleteBranch = await service.DeleteMergedBranchAsync(repo, "feature/safe-git");
        var createTag = await service.CreateTagAsync(repo, "local-check", "local tag");
        var tags = await service.GetTagsAsync(repo);
        var deleteTag = await service.DeleteLocalTagAsync(repo, "local-check");

        Assert.True(create.Succeeded, create.Error);
        Assert.True(switchBack.Succeeded, switchBack.Error);
        Assert.True(deleteBranch.Succeeded, deleteBranch.Error);
        Assert.True(createTag.Succeeded, createTag.Error);
        Assert.Contains("local-check", tags.Value!.Select(tag => tag.Name));
        Assert.True(deleteTag.Succeeded, deleteTag.Error);
    }

    [Fact]
    public async Task Push_SetsUpstreamForFirstPushToOrigin()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("PushRepo");
        var bare = sandbox.PathOf("remote.git");
        await sandbox.RunOk(sandbox.Root, "init", "--bare", bare);
        await sandbox.CommitAsync(repo, "base.txt");
        await sandbox.RunOk(repo, "remote", "add", "origin", bare);

        var result = await new GitService().PushAsync(repo);
        var status = await new GitService().GetStatusAsync(repo);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("origin/" + status.Value!.Branch, status.Value.Upstream);
    }

    [Fact]
    public async Task DetectsAndAbortsMergeConflict()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("ConflictRepo");
        await sandbox.WriteAndCommitAsync(repo, ("shared.txt", "base"));
        var service = new GitService();
        var main = (await service.RunAsync(repo, CancellationToken.None, "symbolic-ref", "--short", "HEAD")).Output.Trim();
        Assert.True((await service.CreateBranchAsync(repo, "feature")).Succeeded);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "shared.txt"), "feature");
        await sandbox.RunOk(repo, "add", "--", "shared.txt");
        await sandbox.RunOk(repo, "commit", "-m", "feature change");
        Assert.True((await service.SwitchBranchAsync(repo, main)).Succeeded);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "shared.txt"), "main");
        await sandbox.RunOk(repo, "add", "--", "shared.txt");
        await sandbox.RunOk(repo, "commit", "-m", "main change");
        var merge = await sandbox.RunAsync(repo, "merge", "feature");
        Assert.NotEqual(0, merge.ExitCode);

        var conflict = await service.GetConflictStateAsync(repo);
        var abort = await service.AbortMergeAsync(repo);
        var finalStatus = await service.GetStatusAsync(repo);

        Assert.True(conflict.Operation.Succeeded);
        Assert.Equal("merge", conflict.Value!.Operation);
        Assert.Contains("shared.txt", conflict.Value.ConflictedPaths);
        Assert.True(abort.Succeeded, abort.Error);
        Assert.Empty(finalStatus.Value!.Files);
    }

    [Fact]
    public async Task ContinueMerge_UsesResolvedFilesAndPreparedMessage()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("ContinueMergeRepo");
        await sandbox.WriteAndCommitAsync(repo, ("shared.txt", "base"));
        var service = new GitService();
        var main = (await service.RunAsync(repo, CancellationToken.None, "symbolic-ref", "--short", "HEAD")).Output.Trim();
        Assert.True((await service.CreateBranchAsync(repo, "feature")).Succeeded);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "shared.txt"), "feature");
        await sandbox.RunOk(repo, "add", "--", "shared.txt");
        await sandbox.RunOk(repo, "commit", "-m", "feature change");
        Assert.True((await service.SwitchBranchAsync(repo, main)).Succeeded);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "shared.txt"), "main");
        await sandbox.RunOk(repo, "add", "--", "shared.txt");
        await sandbox.RunOk(repo, "commit", "-m", "main change");
        Assert.NotEqual(0, (await sandbox.RunAsync(repo, "merge", "feature")).ExitCode);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "shared.txt"), "resolved");
        await sandbox.RunOk(repo, "add", "--", "shared.txt");

        var result = await service.ContinueMergeAsync(repo);
        var history = await service.GetHistoryAsync(repo);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("resolved", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(repo, "shared.txt")));
        Assert.Contains("Merge branch 'feature'", history.Value!.First().Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareRepositoryHasAValidStatusWithoutAWorkingTree()
    {
        using var sandbox = new GitSandbox();
        var bare = sandbox.PathOf("bare.git");
        await sandbox.RunOk(sandbox.Root, "init", "--bare", bare);

        var status = await new GitService().GetStatusAsync(bare);

        Assert.True(status.Operation.Succeeded, status.Operation.Error);
        Assert.True(status.Value!.IsBareRepository);
        Assert.Empty(status.Value.Files);
    }

    [Fact]
    public async Task AmendedCommitSupportsSignoff()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("AmendRepo");
        await sandbox.CommitAsync(repo, "file.txt");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo, "file.txt"), "updated");
        var service = new GitService();
        Assert.True((await service.StageAsync(repo, new[] { "file.txt" })).Succeeded);

        var result = await service.CommitAsync(repo, "amended subject", amend: true, signOff: true);
        var message = await service.RunAsync(repo, CancellationToken.None, "log", "-1", "--format=%B");

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("amended subject", message.Output, StringComparison.Ordinal);
        Assert.Contains("Signed-off-by: Test <test@example.com>", message.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsCancelledAndTimedOutResults()
    {
        using var sandbox = new GitSandbox();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var cancelledResult = await new GitService().RunAsync(sandbox.Root, cancelled.Token, "--version");
        var timeoutResult = await new GitService(TimeSpan.Zero).RunAsync(sandbox.Root, CancellationToken.None, "--version");

        Assert.Equal(GitFailureKind.Cancelled, cancelledResult.Failure);
        Assert.Equal(GitFailureKind.TimedOut, timeoutResult.Failure);
    }

    [Fact]
    public async Task PullFastForwardOnly_UpdatesFromOriginAndRefusesDivergence()
    {
        using var sandbox = new GitSandbox();
        var remote = sandbox.PathOf("remote.git");
        var local = await sandbox.InitRepoAsync("local");
        await sandbox.RunOk(sandbox.Root, "init", "--bare", remote);
        await sandbox.CommitAsync(local, "base.txt");
        await sandbox.RunOk(local, "remote", "add", "origin", remote);
        await sandbox.RunOk(local, "push", "--set-upstream", "origin", "HEAD");
        var clone = sandbox.PathOf("clone");
        await sandbox.RunOk(sandbox.Root, "clone", remote, clone);
        await sandbox.RunOk(clone, "config", "user.email", "test@example.com");
        await sandbox.RunOk(clone, "config", "user.name", "Test");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(clone, "remote.txt"), "remote change");
        await sandbox.RunOk(clone, "add", "--all");
        await sandbox.RunOk(clone, "commit", "-m", "remote update");
        await sandbox.RunOk(clone, "push");

        var result = await new GitService().PullFastForwardOnlyAsync(local);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("remote change", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(local, "remote.txt")));
    }
}
