using System;
using System.Linq;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using DevDesk.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The Activity page merges two sources that overlap by design: the centre knows
/// this session, the history file knows every launch. These cases pin the two
/// things a user would notice if they were wrong — the same run listed twice,
/// and a restart that silently loses yesterday's runs.
/// </summary>
public class ActivityViewModelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Load_MergesSessionAndStoredRunsWithoutListingTheSameRunTwice()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        var center = new ActivityCenter(new TestClock(), history);

        var handle = center.Submit(Request("Formixa", OperationKind.Build));
        await handle.Completion.WaitAsync(Wait);

        // The centre wrote the finished run to history; the page must show one
        // row for it, not one per source.
        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        Assert.Single(viewModel.Rows);
        Assert.True(viewModel.Rows[0].IsSession);
        Assert.Equal(OperationStatus.Succeeded, viewModel.Rows[0].Status);
    }

    [Fact]
    public void Load_ShowsRunsFromAnEarlierLaunchAndTheyCannotBeRetried()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        history.Add(new BuildRecord(
            @"C:\Code\DevEcosystem\Formixa",
            "Formixa",
            OperationKind.Build,
            OperationStatus.Failed,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(4.2),
            1,
            "error CS1002: ; expected",
            @"C:\logs\formixa.log",
            "Release",
            "x64"));

        var center = new ActivityCenter(new TestClock(), history);
        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        var row = Assert.Single(viewModel.Rows);
        Assert.False(row.IsSession);
        Assert.False(row.CanRetry);
        Assert.False(row.CanStop);
        Assert.True(row.HasLog);
        Assert.Equal("Build", row.KindLabel);
        Assert.Equal("Failed", row.StatusLabel);
        Assert.Equal("4.20 s · exit 1", row.ResultText);
        Assert.Contains("Release · x64", row.DetailText, StringComparison.Ordinal);
        Assert.Equal("error CS1002: ; expected", row.FirstError);
    }

    [Fact]
    public async Task Load_IsNewestFirstAcrossBothSources()
    {
        using var sandbox = new SettingsSandbox();
        var clock = new TestClock();
        var history = new BuildHistory(sandbox.Directory, clock);

        // An older stored run, then a live one: the live run is newer and must
        // be at the top even though it is also in history.
        clock.Advance(TimeSpan.FromHours(1));
        history.Add(new BuildRecord(
            @"C:\Code\DevEcosystem\Old",
            "Old",
            OperationKind.Build,
            OperationStatus.Succeeded,
            clock.GetUtcNow(),
            TimeSpan.FromSeconds(1),
            0,
            null,
            null,
            "Debug",
            "x64"));

        var center = new ActivityCenter(clock, history);
        clock.Advance(TimeSpan.FromHours(1));

        var handle = center.Submit(Request("New", OperationKind.Build));
        await handle.Completion.WaitAsync(Wait);

        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Equal("New", viewModel.Rows[0].ProjectName);
        Assert.Equal("Old", viewModel.Rows[1].ProjectName);
    }

    [Fact]
    public async Task Filters_NarrowByProjectAndKindAndSayHowMany()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        var center = new ActivityCenter(new TestClock(), history);

        await center.Submit(Request("Formixa", OperationKind.Build)).Completion.WaitAsync(Wait);
        await center.Submit(Request("Formixa", OperationKind.Test)).Completion.WaitAsync(Wait);
        await center.Submit(Request("DevTem", OperationKind.Build)).Completion.WaitAsync(Wait);

        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        Assert.Equal(3, viewModel.Rows.Count);
        Assert.Equal("3 runs", viewModel.CountText);
        Assert.Contains("Formixa", viewModel.Projects);
        Assert.Contains("DevTem", viewModel.Projects);

        viewModel.SelectedProject = "Formixa";
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Equal("2 of 3 runs", viewModel.CountText);

        viewModel.SelectedKind = "Test";
        var row = Assert.Single(viewModel.Rows);
        Assert.Equal(OperationKind.Test, row.Kind);
    }

    [Fact]
    public async Task Filters_NoMatchShowsAnEmptyStateThatNamesTheFilter()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        var center = new ActivityCenter(new TestClock(), history);

        await center.Submit(Request("Formixa", OperationKind.Build)).Completion.WaitAsync(Wait);

        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        viewModel.SelectedKind = "Publish";

        Assert.Empty(viewModel.Rows);
        Assert.Equal(Visibility.Visible, viewModel.EmptyVisibility);
        Assert.Contains("No run matches this filter", viewModel.EmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithNothingAnywhereShowsTheFirstRunEmptyState()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        var center = new ActivityCenter(new TestClock(), history);

        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        Assert.Empty(viewModel.Rows);
        Assert.Equal(Visibility.Visible, viewModel.EmptyVisibility);
        Assert.Contains("Nothing has run yet", viewModel.EmptyText, StringComparison.Ordinal);
        Assert.Equal("No activity this session.", viewModel.SummaryText);
    }

    [Fact]
    public async Task Summary_CountsOnlyThisSession()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        history.Add(new BuildRecord(
            @"C:\Code\DevEcosystem\Old",
            "Old",
            OperationKind.Build,
            OperationStatus.Failed,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            1,
            "old failure",
            null,
            "Debug",
            "x64"));

        var center = new ActivityCenter(new TestClock(), history);
        await center.Submit(Request("Formixa", OperationKind.Build)).Completion.WaitAsync(Wait);

        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        Assert.StartsWith("1 run this session · 1 succeeded", viewModel.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", viewModel.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_ReturnsTheRowForAnIdAndNullForAStoredOne()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        var center = new ActivityCenter(new TestClock(), history);

        var handle = center.Submit(Request("Formixa", OperationKind.Build));
        await handle.Completion.WaitAsync(Wait);

        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        Assert.NotNull(viewModel.Find(handle.Id));
        Assert.Null(viewModel.Find("op-999"));
        Assert.Null(viewModel.Find(null));
    }

    [Fact]
    public async Task Retry_QueuesTheSameCommandAgainAndGrowsTheList()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        var center = new ActivityCenter(new TestClock(), history);

        var handle = center.Submit(Request("Formixa", OperationKind.Build, fail: true));
        await handle.Completion.WaitAsync(Wait);

        var viewModel = new ActivityViewModel(center, history);
        viewModel.Load();

        var retry = viewModel.Retry(handle.Id);
        Assert.NotNull(retry);
        await retry!.Completion.WaitAsync(Wait);

        Assert.Null(viewModel.Retry("op-999"));
        Assert.Equal(2, center.Operations.Count);
    }

    private static OperationRequest Request(string project, OperationKind kind, bool fail = false) =>
        new(
            kind,
            project,
            $@"C:\Code\DevEcosystem\{project}",
            $"{kind} {project}",
            "dotnet build Formixa.sln -c Debug -p:Platform=x64",
            (_, _) => Task.FromResult(fail
                ? OperationOutcome.Failed(1, "error CS1002: ; expected")
                : OperationOutcome.Succeeded(0)),
            Configuration: "Debug",
            Platform: "x64");
}
