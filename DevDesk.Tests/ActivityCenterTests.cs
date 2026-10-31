using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The activity centre is the one place that decides what runs and what waits, so
/// these cases pin the promises the UI makes: a second build is a visible Queued
/// row, Stop finishes a queued row without starting it, a stopped build is not a
/// failure, and Retry runs the same command again.
/// </summary>
public class ActivityCenterTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Submit_RunsOneOperationAtATimeAndShowsTheSecondAsQueued()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = center.Submit(Request("first", async (_, _) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.ConfigureAwait(false);
            return OperationOutcome.Succeeded(0);
        }));

        await firstStarted.Task.WaitAsync(Wait);

        var second = center.Submit(Request("second", (_, _) =>
        {
            secondStarted.TrySetResult();
            return Task.FromResult(OperationOutcome.Succeeded(0));
        }));

        // The gate is the whole point: the second request is a real row that has
        // not started, not a second compiler behind the first one's back.
        Assert.Equal(OperationStatus.Queued, center.Find(second.Id)!.Status);
        Assert.Equal(1, center.QueuedCount);
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.TrySetResult();

        await first.Completion.WaitAsync(Wait);
        var finishedSecond = await second.Completion.WaitAsync(Wait);

        Assert.Equal(OperationStatus.Succeeded, finishedSecond.Status);
        Assert.True(secondStarted.Task.IsCompleted);
        Assert.Equal(0, center.QueuedCount);
    }

    [Fact]
    public async Task Cancel_AQueuedOperationFinishesItWithoutEverRunningIt()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;

        var first = center.Submit(Request("first", async (_, _) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.ConfigureAwait(false);
            return OperationOutcome.Succeeded(0);
        }));

        await firstStarted.Task.WaitAsync(Wait);

        var second = center.Submit(Request("second", (_, _) =>
        {
            secondRan = true;
            return Task.FromResult(OperationOutcome.Succeeded(0));
        }));

        Assert.True(center.Cancel(second.Id));

        var cancelled = await second.Completion.WaitAsync(Wait);
        Assert.Equal(OperationStatus.Canceled, cancelled.Status);
        Assert.Equal("Cancelled before it started.", cancelled.Hint);

        releaseFirst.TrySetResult();
        await first.Completion.WaitAsync(Wait);

        Assert.False(secondRan);
        Assert.Equal(0, center.FailureCount);
    }

    [Fact]
    public async Task Cancel_ARunningOperationIsCanceledAndIsNotAFailure()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = center.Submit(Request("slow", async (token, _) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return OperationOutcome.Succeeded(0);
        }));

        await started.Task.WaitAsync(Wait);
        Assert.Equal(OperationStatus.Running, center.Find(handle.Id)!.Status);

        Assert.True(center.Cancel(handle.Id));

        var finished = await handle.Completion.WaitAsync(Wait);
        Assert.Equal(OperationStatus.Canceled, finished.Status);
        Assert.Equal(0, center.FailureCount);
        Assert.NotNull(finished.FinishedAt);
    }

    [Fact]
    public async Task Cancel_AnAlreadyFinishedOperationReturnsFalse()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var handle = center.Submit(Request("quick", (_, _) => Task.FromResult(OperationOutcome.Succeeded(0))));
        await handle.Completion.WaitAsync(Wait);

        Assert.False(center.Cancel(handle.Id));
    }

    [Fact]
    public async Task Retry_QueuesANewOperationWithTheSameCommand()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var runs = 0;
        var handle = center.Submit(Request("build", (_, _) =>
        {
            runs++;
            return Task.FromResult(OperationOutcome.Failed(1, "error CS1002: ; expected"));
        }));

        var first = await handle.Completion.WaitAsync(Wait);
        Assert.Equal(OperationStatus.Failed, first.Status);

        var retry = center.Retry(handle.Id);
        Assert.NotNull(retry);
        Assert.NotEqual(handle.Id, retry!.Id);

        var second = await retry.Completion.WaitAsync(Wait);

        Assert.Equal(2, runs);
        Assert.Equal(OperationStatus.Failed, second.Status);
        Assert.Equal(first.Command, second.Command);
        Assert.Equal(first.Title, second.Title);
    }

    [Fact]
    public async Task Retry_AnUnknownIdReturnsNull()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        Assert.Null(center.Retry("op-999"));
    }

    [Fact]
    public async Task Complete_RecordsTheOperationInHistory()
    {
        using var sandbox = new SettingsSandbox();
        var clock = new TestClock();
        var history = new BuildHistory(sandbox.Directory, clock);
        var center = new ActivityCenter(clock, history);

        var handle = center.Submit(Request("build", async (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2.5));
            return OperationOutcome.Succeeded(0, @"C:\logs\build.log");
        }));

        var finished = await handle.Completion.WaitAsync(Wait);

        Assert.Equal(TimeSpan.FromSeconds(2.5), finished.Duration);

        var record = history.LastFor(@"C:\Code\DevEcosystem\Formixa");
        Assert.NotNull(record);
        Assert.Equal(OperationKind.Build, record!.Kind);
        Assert.Equal(OperationStatus.Succeeded, record.Status);
        Assert.Equal(TimeSpan.FromSeconds(2.5), record.Duration);
        Assert.Equal(@"C:\logs\build.log", record.LogPath);
        Assert.Equal("Debug", record.Configuration);
        Assert.Equal("x64", record.Platform);
    }

    [Fact]
    public async Task Complete_AFailureIncrementsTheSessionFailureCount()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var handle = center.Submit(Request("build", (_, _) =>
            Task.FromResult(OperationOutcome.Failed(1, "error CS1002: ; expected"))));

        var finished = await handle.Completion.WaitAsync(Wait);

        Assert.Equal(1, center.FailureCount);
        Assert.Equal("error CS1002: ; expected", finished.FirstError);
        Assert.Equal(1, finished.ExitCode);
    }

    [Fact]
    public async Task Submit_AnUnexpectedExceptionBecomesAFailedRowNotACrash()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var handle = center.Submit(Request("build", (_, _) =>
            throw new InvalidOperationException("the tool vanished")));

        var finished = await handle.Completion.WaitAsync(Wait);

        Assert.Equal(OperationStatus.Failed, finished.Status);
        Assert.Contains("the tool vanished", finished.FirstError, StringComparison.Ordinal);
        Assert.Equal(1, center.FailureCount);
    }

    [Fact]
    public async Task Operations_AreNewestFirst()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var first = center.Submit(Request("one", (_, _) => Task.FromResult(OperationOutcome.Succeeded(0))));
        await first.Completion.WaitAsync(Wait);

        var second = center.Submit(Request("two", (_, _) => Task.FromResult(OperationOutcome.Succeeded(0))));
        await second.Completion.WaitAsync(Wait);

        var operations = center.Operations;

        Assert.Equal(2, operations.Count);
        Assert.Equal(second.Id, operations[0].Id);
        Assert.Equal(first.Id, operations[1].Id);
    }

    [Fact]
    public async Task Changed_IsRaisedWhenAnOperationIsQueuedAndFinishes()
    {
        using var sandbox = new SettingsSandbox();
        var center = NewCenter(sandbox);

        var changes = 0;
        center.Changed += (_, _) => Interlocked.Increment(ref changes);

        var handle = center.Submit(Request("build", (_, _) => Task.FromResult(OperationOutcome.Succeeded(0))));
        await handle.Completion.WaitAsync(Wait);

        Assert.True(changes >= 3, $"only {changes} change events were raised");
    }

    private static ActivityCenter NewCenter(SettingsSandbox sandbox) =>
        new(history: new BuildHistory(sandbox.Directory));

    private static OperationRequest Request(
        string title,
        Func<CancellationToken, IProgress<ProcessOutputLine>?, Task<OperationOutcome>> work) =>
        new(
            OperationKind.Build,
            "Formixa",
            @"C:\Code\DevEcosystem\Formixa",
            title,
            "dotnet build Formixa.sln -c Debug -p:Platform=x64",
            work,
            Configuration: "Debug",
            Platform: "x64");
}
