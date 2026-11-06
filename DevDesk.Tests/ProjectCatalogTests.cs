using System;
using System.Collections.Generic;
using System.IO;
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
    public async Task Catalog_PublishesRealProjectsAsTheyResolve()
    {
        var project = Project("Found");
        var engine = new FakeScanEngine
        {
            Gate = new TaskCompletionSource<ScannerResult>(TaskCreationOptions.RunContinuationsAsynchronously),
            ProgressProjects = new[] { project },
        };
        var catalog = new ProjectCatalog(engine);
        var partial = new TaskCompletionSource<CatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.Changed += (_, snapshot) =>
        {
            if (snapshot.Freshness == CatalogFreshness.Partial)
            {
                partial.TrySetResult(snapshot);
            }
        };

        var scan = catalog.RefreshAsync();
        var progressSnapshot = await partial.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CatalogFreshness.Partial, progressSnapshot.Freshness);
        Assert.Equal("Found", Assert.Single(progressSnapshot.Projects).Name);
        Assert.Same(progressSnapshot, catalog.Snapshot);

        engine.Gate.SetResult(engine.Result(project));
        var complete = await scan;
        Assert.Equal(CatalogFreshness.Fresh, complete.Freshness);
        Assert.Equal("Found", Assert.Single(complete.Projects).Name);
    }

    [Fact]
    public async Task Catalog_AccumulatesAndCoalescesProgressSnapshots()
    {
        var projects = Enumerable.Range(0, 11)
            .Select(index => Project($"Project-{index:D2}"))
            .ToArray();
        var engine = new FakeScanEngine
        {
            Gate = new TaskCompletionSource<ScannerResult>(TaskCreationOptions.RunContinuationsAsynchronously),
            ProgressProjects = projects,
        };
        var clock = new TestClock();
        var catalog = new ProjectCatalog(engine, time: clock);
        var partialSnapshots = new List<CatalogSnapshot>();
        catalog.Changed += (_, snapshot) =>
        {
            if (snapshot.Freshness == CatalogFreshness.Partial)
            {
                partialSnapshots.Add(snapshot);
            }
        };
        var reports = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineTestProgress<ScanProgress>(update =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            if (update.Completed == projects.Length)
            {
                reports.TrySetResult();
            }
        });

        var scan = catalog.RefreshAsync(progress: progress);
        await reports.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, partialSnapshots.Count);
        Assert.Equal(projects.Length, Assert.Single(partialSnapshots.TakeLast(1)).Projects.Count);
        Assert.Equal(projects.Length, catalog.Snapshot.Projects.Count);

        engine.Gate.SetResult(engine.Result(projects));
        var complete = await scan;
        Assert.Equal(projects.Length, complete.Projects.Count);
    }

    [Fact]
    public async Task Catalog_CancelCurrentScanStopsWorkAndRestoresLastPublishedSnapshot()
    {
        var engine = new FakeScanEngine
        {
            Gate = new TaskCompletionSource<ScannerResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var catalog = new ProjectCatalog(engine);

        var scan = catalog.RefreshAsync();
        await Task.Yield();

        Assert.True(catalog.CancelCurrentScan());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);

        Assert.False(catalog.IsRefreshing);
        Assert.True(catalog.Snapshot.IsEmpty);
    }

    [Fact]
    public async Task Catalog_RootRefreshReplacesOnlyProjectsOwnedByThatRoot()
    {
        var rootA = @"C:\scan\A";
        var rootB = @"C:\scan\B";
        var engine = new FakeScanEngine
        {
            Projects = new[]
            {
                WithRoot(Project("A-old"), rootA),
                WithRoot(Project("B-keep"), rootB),
            },
            RootScan = root => root.Path == rootA
                ? new ScannerResult { Projects = new[] { WithRoot(Project("A-new"), rootA) } }
                : new ScannerResult(),
        };
        var catalog = new ProjectCatalog(engine);
        await catalog.RefreshAsync();

        var snapshot = await catalog.RefreshRootAsync(
            new ScanRootSettings { Path = rootA },
            new[] { new ScanRootSettings { Path = rootA }, new ScanRootSettings { Path = rootB } });

        Assert.Equal(1, engine.Calls);
        Assert.Equal(1, engine.RootCalls);
        Assert.Equal(new[] { "A-new", "B-keep" }, snapshot.Projects.Select(project => project.Name));
    }

    [Fact]
    public async Task Catalog_CheckoutRefreshReplacesOnlyThatCheckout()
    {
        var root = @"C:\scan\A";
        var checkoutPath = Path.Combine(root, "A");
        var retained = WithRoot(Project("B-keep"), root);
        var original = new ProjectModel
        {
            Name = "A-old",
            Path = checkoutPath,
            ScanRootPath = root,
            Branch = "main",
            Version = "1.0.0",
        };
        var updated = new ProjectModel
        {
            Name = "A-updated",
            Path = checkoutPath,
            ScanRootPath = root,
            Branch = "main",
            Version = "1.1.0",
        };
        var engine = new FakeScanEngine
        {
            Projects = new[] { original, retained },
            CheckoutScan = (_, path) => path == checkoutPath ? updated : null,
        };
        var catalog = new ProjectCatalog(engine);
        await catalog.RefreshAsync();

        var snapshot = await catalog.RefreshCheckoutAsync(
            new ScanRootSettings { Path = root },
            checkoutPath);

        Assert.Equal(1, engine.Calls);
        Assert.Equal(0, engine.RootCalls);
        Assert.Equal(1, engine.CheckoutCalls);
        Assert.Equal(new[] { "A-updated", "B-keep" }, snapshot.Projects.Select(project => project.Name));
    }

    [Fact]
    public async Task Catalog_PartialScanKeepsLastKnownCardsForUnavailableRoots()
    {
        var rootA = @"C:\scan\A";
        var rootB = @"C:\scan\B";
        var engine = new FakeScanEngine
        {
            Projects = new[] { WithRoot(Project("A-last-known"), rootA) },
        };
        var catalog = new ProjectCatalog(engine);
        await catalog.RefreshAsync();

        engine.Projects = new[] { WithRoot(Project("B-fresh"), rootB) };
        engine.RootDiagnostics = new[] { new ScanRootDiagnostic(rootA, string.Empty, "Root unavailable") };
        var snapshot = await catalog.RefreshAsync();

        Assert.Equal(CatalogFreshness.Partial, snapshot.Freshness);
        Assert.Contains(snapshot.Projects, project => project.Name == "A-last-known");
        Assert.Contains(snapshot.Projects, project => project.Name == "B-fresh");
        Assert.Equal("Root unavailable", Assert.Single(snapshot.RootDiagnostics).Message);
    }

    private static ProjectModel WithRoot(ProjectModel project, string root) =>
        new()
        {
            Name = project.Name,
            Path = Path.Combine(root, project.Name),
            ScanRootPath = root,
            Branch = project.Branch,
            Version = project.Version,
            HasRemote = project.HasRemote,
            RemoteOk = project.RemoteOk,
            Dirty = project.Dirty,
            BehindUpstream = project.BehindUpstream,
            StatusText = project.StatusText,
        };

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

    [Fact]
    public async Task Catalog_CachedSnapshotPaintsImmediatelyThenRefreshesFromScanner()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        var cachedPath = Path.Combine(root, "Cached");
        Directory.CreateDirectory(cachedPath);
        var cache = new ScanCacheStore(new JsonStore(Path.Combine(sandbox.Directory, "scan-cache.json")));
        cache.Save(new CatalogSnapshot(new[]
        {
            new ProjectModel
            {
                Name = "Cached",
                Path = cachedPath,
                Branch = "cached-branch",
                Version = "1.0.0",
                HasRemote = false,
                RemoteOk = false,
                RemoteHost = "Local only",
            },
        }, null, capturedAt: DateTimeOffset.UtcNow));
        var engine = new FakeScanEngine { Projects = new[] { Project("Fresh") } };
        var catalog = new ProjectCatalog(
            engine,
            cache: cache,
            cacheRoots: new[] { new ScanRootSettings { Path = root } });

        Assert.Equal(CatalogFreshness.Cached, catalog.Snapshot.Freshness);
        Assert.Equal("Cached", Assert.Single(catalog.Snapshot.Projects).Name);
        Assert.Equal(0, engine.Calls);

        var fresh = await catalog.GetOrRefreshAsync();

        Assert.Equal(CatalogFreshness.Fresh, fresh.Freshness);
        Assert.Equal("Fresh", Assert.Single(fresh.Projects).Name);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task Catalog_FailedRefreshMarksLastGoodSnapshotStale()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        var cachedPath = Path.Combine(root, "Cached");
        Directory.CreateDirectory(cachedPath);
        var cache = new ScanCacheStore(new JsonStore(Path.Combine(sandbox.Directory, "scan-cache.json")));
        cache.Save(new CatalogSnapshot(new[]
        {
            new ProjectModel
            {
                Name = "Cached",
                Path = cachedPath,
                Branch = "main",
                Version = "1.0.0",
                HasRemote = false,
                RemoteOk = false,
                RemoteHost = "Local only",
            },
        }, null, capturedAt: DateTimeOffset.UtcNow));
        var catalog = new ProjectCatalog(
            new FakeScanEngine { Error = new InvalidOperationException("scan failed") },
            cache: cache,
            cacheRoots: new[] { new ScanRootSettings { Path = root } });

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetOrRefreshAsync());

        Assert.Equal(CatalogFreshness.Stale, catalog.Snapshot.Freshness);
        Assert.Equal("Cached", Assert.Single(catalog.Snapshot.Projects).Name);
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

    private sealed class InlineTestProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>A scan engine whose timing and outcome the test controls.</summary>
internal sealed class FakeScanEngine : IScanRootEngine, IScanCheckoutEngine
{
    public string ScanRoot => @"C:\fake\ecosystem";

    public int Calls { get; private set; }

    public IReadOnlyList<ProjectModel> Projects { get; set; } = Array.Empty<ProjectModel>();

    public ProjectModel? Self { get; set; }

    public TaskCompletionSource<ScannerResult>? Gate { get; set; }

    public Exception? Error { get; set; }

    public IReadOnlyList<ProjectModel> ProgressProjects { get; set; } = Array.Empty<ProjectModel>();

    public Func<ScanRootSettings, ScannerResult>? RootScan { get; set; }

    public IReadOnlyList<ScanRootDiagnostic> RootDiagnostics { get; set; } = Array.Empty<ScanRootDiagnostic>();

    public int RootCalls { get; private set; }

    public Func<ScanRootSettings, string, ProjectModel?>? CheckoutScan { get; set; }

    public int CheckoutCalls { get; private set; }

    public Task<ScannerResult> ScanAsync(
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        Calls++;

        if (Error is not null)
        {
            return Task.FromException<ScannerResult>(Error);
        }

        for (var index = 0; index < ProgressProjects.Count; index++)
        {
            var project = ProgressProjects[index];
            progress?.Report(new ScanProgress(
                index + 1,
                ProgressProjects.Count,
                project.Name,
                project.ScanRootPath,
                project));
        }

        return Gate is null
            ? Task.FromResult(Result(Projects.ToArray()))
            : Gate.Task.WaitAsync(cancellationToken);
    }

    public ScannerResult Result(params ProjectModel[] projects) => new()
    {
        Projects = projects.Length > 0 ? projects : Projects,
        Self = Self,
        RootDiagnostics = RootDiagnostics,
    };

    public Task<ScannerResult> ScanRootAsync(
        ScanRootSettings root,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        RootCalls++;
        return Task.FromResult(RootScan?.Invoke(root) ?? new ScannerResult());
    }

    public Task<ProjectModel?> ScanCheckoutAsync(
        ScanRootSettings root,
        string checkoutPath,
        CancellationToken cancellationToken = default)
    {
        CheckoutCalls++;
        return Task.FromResult(CheckoutScan?.Invoke(root, checkoutPath));
    }
}
