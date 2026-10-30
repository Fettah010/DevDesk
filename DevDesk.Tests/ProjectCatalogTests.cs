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
/// The catalog is what makes "scan once, render everywhere" true. These tests
/// drive it with a fake engine, so they never touch git and never wait on a
/// real disk.
/// </summary>
public class ProjectCatalogTests
{
    [Fact]
    public async Task Catalog_SecondRefreshJoinsInFlight()
    {
        var engine = new FakeScanEngine { Gate = new TaskCompletionSource<ScannerResult>() };
        var catalog = new ProjectCatalog(engine);

        var first = catalog.RefreshAsync();
        var second = catalog.RefreshAsync();

        Assert.Same(first, second);
        Assert.True(catalog.IsRefreshing);

        engine.Gate.SetResult(engine.Result(Project("A")));
        await first;

        Assert.False(catalog.IsRefreshing);
        Assert.Equal(1, catalog.ScanCount);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task Catalog_RefreshAfterCompletion_RunsAgain()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);

        await catalog.RefreshAsync();
        await catalog.RefreshAsync();

        Assert.Equal(2, catalog.ScanCount);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task Catalog_ScanCountIncrementsOncePerScan()
    {
        var engine = new FakeScanEngine { Gate = new TaskCompletionSource<ScannerResult>() };
        var catalog = new ProjectCatalog(engine);

        var scans = new[] { catalog.RefreshAsync(), catalog.RefreshAsync(), catalog.RefreshAsync() };

        engine.Gate.SetResult(engine.Result(Project("A")));
        await Task.WhenAll(scans);

        Assert.Equal(1, catalog.ScanCount);
    }

    [Fact]
    public async Task Catalog_SnapshotIsImmutable()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);

        var snapshot = await catalog.RefreshAsync();
        var projects = Assert.IsAssignableFrom<IList<ProjectModel>>(snapshot.Projects);

        Assert.True(projects.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => projects.Add(Project("B")));
        Assert.Throws<NotSupportedException>(() => projects.Clear());
    }

    [Fact]
    public async Task Catalog_ChangedRaisedOnNewSnapshot()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);

        var seen = new List<CatalogSnapshot>();
        catalog.Changed += (_, snapshot) => seen.Add(snapshot);

        var result = await catalog.RefreshAsync();

        var raised = Assert.Single(seen);
        Assert.Same(result, raised);
        Assert.Same(catalog.Snapshot, raised);
        Assert.True(raised.CapturedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task Catalog_GetOrRefreshAsync_ReusesTheSnapshot()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);

        await catalog.GetOrRefreshAsync();
        var second = await catalog.GetOrRefreshAsync();
        var third = await catalog.GetOrRefreshAsync();

        Assert.Equal(1, catalog.ScanCount);
        Assert.Same(second, third);
    }

    [Fact]
    public async Task Catalog_EmptyEcosystem_StillCountsAsScanned()
    {
        // No projects found is a valid answer, not "never scanned": a second
        // page visit must not walk the disk again.
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);

        var first = await catalog.GetOrRefreshAsync();
        var second = await catalog.GetOrRefreshAsync();

        Assert.True(first.IsEmpty);
        Assert.Same(first, second);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task Catalog_FailedScan_LeavesNoHalfPopulatedSnapshot()
    {
        var engine = new FakeScanEngine { Error = new InvalidOperationException("boom") };
        var catalog = new ProjectCatalog(engine);

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RefreshAsync());

        Assert.True(catalog.Snapshot.IsEmpty);
        Assert.False(catalog.IsRefreshing);
        Assert.Equal(1, catalog.ScanCount);
    }

    [Fact]
    public async Task Catalog_CancelledScan_KeepsThePreviousSnapshot()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);
        var good = await catalog.RefreshAsync();

        engine.Error = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.RefreshAsync());

        Assert.Same(good, catalog.Snapshot);
        Assert.False(catalog.IsRefreshing);
    }

    [Fact]
    public async Task Catalog_ComputesSummaryCounts()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);
        engine.Projects = new[]
        {
            Project("clean", dirty: false, behind: false),
            Project("dirty", dirty: true, behind: false),
            Project("behind", dirty: false, behind: true),
            Project("local", hasRemote: false),
        };

        var snapshot = await catalog.GetOrRefreshAsync();

        Assert.Equal(1, snapshot.CleanCount);
        Assert.Equal(3, snapshot.AttentionCount);
        Assert.Equal(3, snapshot.RemoteCount);
    }

    [Fact]
    public async Task Catalog_UpdateProjectPublishesOneLocalRevisionWithoutAnotherScan()
    {
        var engine = new FakeScanEngine { Projects = new[] { Project("A") } };
        var catalog = new ProjectCatalog(engine);
        var original = await catalog.GetOrRefreshAsync();
        var updatedProject = new ProjectModel
        {
            Name = "A",
            Path = original.Projects[0].Path,
            Branch = "feature",
            Version = "1.0.0",
            HasRemote = true,
            RemoteOk = true,
            Dirty = true,
            StatusText = "Changes",
        };
        CatalogSnapshot? changed = null;
        catalog.Changed += (_, snapshot) => changed = snapshot;

        var replaced = catalog.UpdateProject(updatedProject);

        Assert.True(replaced);
        Assert.NotSame(original, catalog.Snapshot);
        Assert.Equal("feature", Assert.Single(catalog.Snapshot.Projects).Branch);
        Assert.True(Assert.Single(catalog.Snapshot.Projects).Dirty);
        Assert.Same(catalog.Snapshot, changed);
        Assert.Equal(1, catalog.ScanCount);
    }

    internal static ProjectModel Project(
        string name,
        bool dirty = false,
        bool behind = false,
        bool hasRemote = true) =>
        new()
        {
            Name = name,
            Path = "/tmp/" + name,
            Branch = "main",
            Version = "1.0.0",
            HasRemote = hasRemote,
            RemoteOk = hasRemote,
            Dirty = dirty,
            BehindUpstream = behind,
            StatusText = dirty ? "Uncommitted changes" : "Clean working tree",
        };
}

/// <summary>A scan engine whose timing and outcome the test controls.</summary>
internal sealed class FakeScanEngine : IScanEngine
{
    public string ScanRoot => @"C:\fake\ecosystem";

    public int Calls { get; private set; }

    public IReadOnlyList<ProjectModel> Projects { get; set; } = Array.Empty<ProjectModel>();

    public ProjectModel? Self { get; set; }

    public TaskCompletionSource<ScannerResult>? Gate { get; set; }

    public Exception? Error { get; set; }

    public Task<ScannerResult> ScanAsync(
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        Calls++;

        if (Error is not null)
        {
            return Task.FromException<ScannerResult>(Error);
        }

        return Gate is null
            ? Task.FromResult(Result(Projects.ToArray()))
            : Gate.Task;
    }

    public ScannerResult Result(params ProjectModel[] projects) => new()
    {
        Projects = projects.Length > 0 ? projects : Projects,
        Self = Self,
    };
}
