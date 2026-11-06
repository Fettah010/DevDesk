using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ProjectListQueryTests
{
    [Fact]
    public void Query_SearchesNamePathBranchAndVersionWithoutMutatingSource()
    {
        var source = new[]
        {
            Project("Alpha", @"C:\work\alpha", "main", "1.0.0"),
            Project("Beta", @"D:\code\product", "feature/login", "2.0.0-beta"),
            Project("Gamma", @"C:\work\gamma", "main", "3.0.0"),
        };

        Assert.Equal("Beta", Assert.Single(ProjectListQuery.Apply(source, "login", ProjectSortOrder.Name)).Name);
        Assert.Equal("Beta", Assert.Single(ProjectListQuery.Apply(source, @"D:\code", ProjectSortOrder.Name)).Name);
        Assert.Equal("Beta", Assert.Single(ProjectListQuery.Apply(source, "2.0.0-beta", ProjectSortOrder.Name)).Name);
        Assert.Equal(3, source.Length);
    }

    [Fact]
    public void Query_SearchesUserTagsWithoutChangingRepositoryFacts()
    {
        using var sandbox = new SettingsSandbox();
        var project = Project("Beta", @"D:\code\product");
        var organization = new ProjectOrganizationStore(sandbox.Directory);
        Assert.True(organization.SetTags(project.Path, new[] { "platform", "internal tools" }));

        Assert.Same(project, Assert.Single(ProjectListQuery.Apply(
            new[] { project },
            "internal",
            ProjectSortOrder.Name,
            organization.Snapshot)));
        Assert.Equal("Beta", project.Name);
    }

    [Fact]
    public void Query_NameSortIsCaseInsensitiveAndStableByName()
    {
        var projects = new[] { Project("zeta"), Project("ALPHA"), Project("alpha") };

        Assert.Equal(
            new[] { "ALPHA", "alpha", "zeta" },
            ProjectListQuery.Apply(projects, null, ProjectSortOrder.Name).Select(project => project.Name));
    }

    [Fact]
    public void Query_ActivitySortUsesCommitTimestampAndPlacesUnknownLast()
    {
        var projects = new[]
        {
            Project("No activity"),
            Project("Older", committedAt: DateTimeOffset.Parse("2025-01-01T00:00:00Z")),
            Project("Newer", committedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
        };

        Assert.Equal(
            new[] { "Newer", "Older", "No activity" },
            ProjectListQuery.Apply(projects, null, ProjectSortOrder.Activity).Select(project => project.Name));
    }

    [Fact]
    public void Query_HealthSortPutsCleanProjectsFirst()
    {
        var projects = new[]
        {
            Project("Dirty", dirty: true),
            Project("Behind", behind: true),
            Project("Clean"),
        };

        Assert.Equal(
            new[] { "Clean", "Behind", "Dirty" },
            ProjectListQuery.Apply(projects, null, ProjectSortOrder.Health).Select(project => project.Name));
    }

    [Fact]
    public void Query_VersionSortComparesNumericVersionsAndStableBeforePrerelease()
    {
        var projects = new[]
        {
            Project("v10", version: "10.0.0"),
            Project("v2beta", version: "2.0.0-beta"),
            Project("v2", version: "2.0.0"),
            Project("unknown", version: "—"),
        };

        Assert.Equal(
            new[] { "v10", "v2", "v2beta", "unknown" },
            ProjectListQuery.Apply(projects, null, ProjectSortOrder.Version).Select(project => project.Name));
    }

    [Fact]
    public void Query_PinnedProjectsStayFirstAndRecentlyOpenedSortUsesUserMetadata()
    {
        using var sandbox = new SettingsSandbox();
        var alphaPath = @"C:\work\alpha";
        var betaPath = @"C:\work\beta";
        var store = new ProjectOrganizationStore(sandbox.Directory);
        Assert.True(store.MarkOpened(alphaPath));
        Assert.True(store.MarkOpened(betaPath));
        Assert.True(store.SetPinned(betaPath, true));

        var projects = new[]
        {
            Project("Alpha", alphaPath),
            Project("Beta", betaPath),
            Project("No activity", @"C:\work\unknown"),
        };

        Assert.Equal(
            new[] { "Beta", "Alpha", "No activity" },
            ProjectListQuery.Apply(projects, null, ProjectSortOrder.RecentlyOpened, store.Snapshot)
                .Select(project => project.Name));
    }

    [Fact]
    public void Query_UsesConfiguredRootAsStableNameTieBreakAndHidesUserArchivedProjects()
    {
        using var sandbox = new SettingsSandbox();
        var rootA = @"C:\scan\A";
        var rootB = @"C:\scan\B";
        var first = Project("Same", @"C:\scan\A\repo", scanRootPath: rootA);
        var second = Project("Same", @"C:\scan\B\repo", scanRootPath: rootB);
        var store = new ProjectOrganizationStore(sandbox.Directory);
        Assert.True(store.SetArchived(first.Path, true));

        var ordered = ProjectListQuery.Apply(
            new[] { first, second },
            null,
            ProjectSortOrder.Name,
            store.Snapshot,
            new[] { new ScanRootSettings { Path = rootB }, new ScanRootSettings { Path = rootA } },
            hideArchived: true);

        Assert.Equal(second.Path, Assert.Single(ordered).Path);
    }

    [Fact]
    [Trait("Category", "V07Benchmarks")]
    public void Query_FiftyCardSearchAveragesUnderFiftyMilliseconds()
    {
        var projects = Enumerable.Range(0, 50)
            .Select(index => Project($"Project-{index:D2}", $@"C:\work\Project-{index:D2}", "main", "1.0.0"))
            .ToArray();
        _ = ProjectListQuery.Apply(projects, "Project-2", ProjectSortOrder.Name);
        const int iterations = 100;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++)
        {
            _ = ProjectListQuery.Apply(projects, "Project-2", ProjectSortOrder.Name);
        }

        var average = Stopwatch.GetElapsedTime(started) / iterations;
        Assert.True(average < TimeSpan.FromMilliseconds(50), $"50-card search averaged {average.TotalMilliseconds:0.000} ms.");
    }

    private static ProjectModel Project(
        string name,
        string? path = null,
        string branch = "main",
        string version = "1.0.0",
        bool dirty = false,
        bool behind = false,
        DateTimeOffset? committedAt = null,
        string scanRootPath = "") =>
        new()
        {
            Name = name,
            Path = path ?? @"C:\work\" + name,
            ScanRootPath = scanRootPath,
            Branch = branch,
            Version = version,
            Dirty = dirty,
            BehindUpstream = behind,
            HasRemote = true,
            RemoteOk = true,
            RecentCommits = committedAt is null
                ? Array.Empty<CommitInfo>()
                : new[]
                {
                    new CommitInfo
                    {
                        Subject = "commit",
                        Author = "Author",
                        Date = "today",
                        Hash = "abc123",
                        CommittedAt = committedAt,
                    },
                },
        };
}
