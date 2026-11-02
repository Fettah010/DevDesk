using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleaseWorkflowMonitorTests
{
    private static readonly GitHubRepositoryIdentity Identity = new("github.com", "owner", "repo");
    private const string Tag = "v0.6.1-beta";
    private static readonly string Sha = new('a', 40);

    [Fact]
    public async Task MonitorAsync_TracksOnlyTheExactReleaseRunUntilCompletion()
    {
        var unrelated = Run(1, "in_progress", null, branch: "main", sha: new string('b', 40));
        var queued = Run(2, "queued", null);
        var running = Run(2, "in_progress", null);
        var succeeded = Run(2, "completed", "success");
        var reader = new FakeReader(
            Success(unrelated),
            Success(queued),
            Success(running),
            Success(succeeded));
        var clock = new TestClock();
        var delays = new List<TimeSpan>();
        var updates = new List<ReleaseWorkflowMonitorUpdate>();
        var monitor = CreateMonitor(reader, clock, (delay, _) =>
        {
            delays.Add(delay);
            clock.Advance(delay);
            return Task.CompletedTask;
        });

        var result = await monitor.MonitorAsync(Identity, Tag, Sha, new RecordingProgress(updates));

        Assert.Equal(ReleaseWorkflowMonitorState.Succeeded, result.State);
        Assert.Equal("https://github.com/owner/repo/actions/runs/2", result.RunUrl);
        Assert.Equal(4, reader.Requests);
        Assert.All(reader.ForceRefreshValues, Assert.True);
        Assert.Equal(
            [
                ReleaseWorkflowMonitorState.AwaitingRun,
                ReleaseWorkflowMonitorState.Queued,
                ReleaseWorkflowMonitorState.Running,
                ReleaseWorkflowMonitorState.Succeeded
            ],
            updates.Select(update => update.State));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], delays);
    }

    [Theory]
    [InlineData("failure", ReleaseWorkflowMonitorState.Failed)]
    [InlineData("cancelled", ReleaseWorkflowMonitorState.Cancelled)]
    public async Task MonitorAsync_MapsCompletedWorkflowConclusion(
        string conclusion,
        ReleaseWorkflowMonitorState expected)
    {
        var reader = new FakeReader(Success(Run(8, "completed", conclusion)));
        var result = await CreateMonitor(reader, new TestClock(), (_, _) => Task.CompletedTask)
            .MonitorAsync(Identity, Tag, Sha);

        Assert.Equal(expected, result.State);
        Assert.Equal(8, result.Run!.Id);
    }

    [Fact]
    public async Task MonitorAsync_RejectsAmbiguousMatchingRuns()
    {
        var reader = new FakeReader(Success(Run(10, "queued", null), Run(11, "queued", null)));
        var result = await CreateMonitor(reader, new TestClock(), (_, _) => Task.CompletedTask)
            .MonitorAsync(Identity, Tag, Sha);

        Assert.Equal(ReleaseWorkflowMonitorState.Error, result.State);
        Assert.Contains("multiple release runs", result.Message);
        Assert.Equal(1, reader.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/owner/repo/actions/runs/12")]
    [InlineData("http://github.com/owner/repo/actions/runs/12")]
    [InlineData("https://github.com:444/owner/repo/actions/runs/12")]
    [InlineData("https://github.com/other/repo/actions/runs/12")]
    [InlineData("https://github.com/owner/repo/issues/12")]
    [InlineData("https://attacker@github.com/owner/repo/actions/runs/12")]
    public async Task MonitorAsync_RejectsUntrustedOrMismatchedRunLinks(string url)
    {
        var reader = new FakeReader(Success(Run(12, "queued", null) with { HtmlUrl = url }));

        var result = await CreateMonitor(reader, new TestClock(), (_, _) => Task.CompletedTask)
            .MonitorAsync(Identity, Tag, Sha);

        Assert.Equal(ReleaseWorkflowMonitorState.Error, result.State);
        Assert.Null(result.RunUrl);
        Assert.Contains("invalid release workflow link", result.Message);
    }

    [Fact]
    public async Task MonitorAsync_IgnoresRunsFromOtherWorkflowEventsOrTags()
    {
        var nonPush = Run(1, "queued", null) with { Event = "workflow_dispatch" };
        var wrongPath = Run(2, "queued", null) with { Path = ".github/workflows/ci.yml" };
        var wrongTag = Run(3, "queued", null) with { Branch = "v0.6.2-beta" };
        var reader = new FakeReader(Success(nonPush, wrongPath, wrongTag), Success(Run(4, "completed", "success")));
        var clock = new TestClock();
        var result = await CreateMonitor(reader, clock, (delay, _) =>
        {
            clock.Advance(delay);
            return Task.CompletedTask;
        }).MonitorAsync(Identity, Tag, Sha);

        Assert.Equal(ReleaseWorkflowMonitorState.Succeeded, result.State);
        Assert.Equal(2, reader.Requests);
        Assert.Equal(4, result.Run!.Id);
    }

    [Fact]
    public async Task MonitorAsync_StopsAtTheConfiguredMaximumDuration()
    {
        var reader = new FakeReader(Success(), Success());
        var clock = new TestClock();
        var monitor = CreateMonitor(
            reader,
            clock,
            (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            new ReleaseWorkflowMonitorOptions(
                TimeSpan.FromSeconds(5),
                [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)]));
        var updates = new List<ReleaseWorkflowMonitorUpdate>();

        var result = await monitor.MonitorAsync(Identity, Tag, Sha, new RecordingProgress(updates));

        Assert.Equal(ReleaseWorkflowMonitorState.TimedOut, result.State);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Elapsed);
        Assert.Equal(2, reader.Requests);
        Assert.Contains(updates, update => update.State == ReleaseWorkflowMonitorState.AwaitingRun);
    }

    [Fact]
    public async Task MonitorAsync_CancellationStopsMonitoringWithoutCancellingWorkflow()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new FakeReader(Success(Run(14, "in_progress", null)));
        var monitor = CreateMonitor(reader, new TestClock(), (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        });

        var result = await monitor.MonitorAsync(Identity, Tag, Sha, cancellationToken: cancellation.Token);

        Assert.Equal(ReleaseWorkflowMonitorState.MonitoringStopped, result.State);
        Assert.Equal(1, reader.Requests);
        Assert.Equal(14, result.Run!.Id);
        Assert.Equal("https://github.com/owner/repo/actions/runs/14", result.RunUrl);
    }

    [Fact]
    public async Task MonitorAsync_DoesNotTreatStaleCachedWorkflowDataAsCurrent()
    {
        var reader = new FakeReader(new GitHubDataResult<GitHubWorkflowRuns>(
            new GitHubWorkflowRuns([Run(15, "completed", "success")]),
            GitHubErrorKind.None,
            "network unavailable",
            true,
            true,
            DateTimeOffset.UnixEpoch,
            null));

        var result = await CreateMonitor(reader, new TestClock(), (_, _) => Task.CompletedTask)
            .MonitorAsync(Identity, Tag, Sha);

        Assert.Equal(ReleaseWorkflowMonitorState.Error, result.State);
        Assert.Contains("cached workflow data cannot confirm", result.Message);
    }

    [Theory]
    [InlineData(GitHubErrorKind.Forbidden, "Access denied.")]
    [InlineData(GitHubErrorKind.RateLimited, "Wait before retrying.")]
    public async Task MonitorAsync_ReportsGitHubApiFailureWithoutRetrying(
        GitHubErrorKind errorKind,
        string message)
    {
        var reader = new FakeReader(new GitHubDataResult<GitHubWorkflowRuns>(
            null,
            errorKind,
            message,
            false,
            false,
            null,
            null));

        var result = await CreateMonitor(reader, new TestClock(), (_, _) => Task.CompletedTask)
            .MonitorAsync(Identity, Tag, Sha);

        Assert.Equal(ReleaseWorkflowMonitorState.Error, result.State);
        Assert.Equal(message, result.Message);
        Assert.Equal(1, reader.Requests);
    }

    private static ReleaseWorkflowMonitor CreateMonitor(
        FakeReader reader,
        TimeProvider clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        ReleaseWorkflowMonitorOptions? options = null) =>
        new(reader, clock, delay, options);

    private static GitHubWorkflowRun Run(
        long id,
        string status,
        string? conclusion,
        string? branch = Tag,
        string? sha = null) =>
        new(
            id,
            "Release",
            (int)id,
            branch,
            sha ?? Sha,
            status,
            conclusion,
            $"https://github.com/owner/repo/actions/runs/{id}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch)
        {
            Path = $"{ReleaseWorkflowMonitor.WorkflowPath}@refs/tags/{Tag}",
            Event = "push"
        };

    private static GitHubDataResult<GitHubWorkflowRuns> Success(params GitHubWorkflowRun[] runs) =>
        new(new GitHubWorkflowRuns(runs), GitHubErrorKind.None, null, false, false, DateTimeOffset.UnixEpoch, null);

    private sealed class FakeReader(params GitHubDataResult<GitHubWorkflowRuns>[] responses)
        : IReleaseWorkflowRunReader
    {
        private int _nextResponse;

        public int Requests { get; private set; }
        public List<bool> ForceRefreshValues { get; } = [];

        public Task<GitHubDataResult<GitHubWorkflowRuns>> GetWorkflowRunsAsync(
            GitHubRepositoryIdentity identity,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            ForceRefreshValues.Add(forceRefresh);
            var response = responses[Math.Min(_nextResponse, responses.Length - 1)];
            _nextResponse++;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingProgress(List<ReleaseWorkflowMonitorUpdate> updates)
        : IProgress<ReleaseWorkflowMonitorUpdate>
    {
        public void Report(ReleaseWorkflowMonitorUpdate value) => updates.Add(value);
    }
}
