using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// One immutable scan result. Every page renders from the same instance, so the
/// Dashboard and the DevDesk page can never disagree about the same scan.
/// </summary>
public sealed class CatalogSnapshot
{
    public static CatalogSnapshot Empty { get; } = new(
        Array.Empty<ProjectModel>(),
        null,
        templates: Array.Empty<TemplateDescriptor>(),
        DateTimeOffset.MinValue,
        TimeSpan.Zero,
        CatalogFreshness.Unscanned,
        Array.Empty<ScanRootDiagnostic>());

    public CatalogSnapshot(
        IEnumerable<ProjectModel> projects,
        ProjectModel? self,
        IEnumerable<TemplateDescriptor>? templates = null,
        DateTimeOffset capturedAt = default,
        TimeSpan duration = default,
        CatalogFreshness freshness = CatalogFreshness.Fresh,
        IEnumerable<ScanRootDiagnostic>? rootDiagnostics = null)
    {
        Projects = new ReadOnlyCollection<ProjectModel>(new List<ProjectModel>(projects));
        Self = self;
        Templates = new ReadOnlyCollection<TemplateDescriptor>(new List<TemplateDescriptor>(templates ?? Array.Empty<TemplateDescriptor>()));
        CapturedAt = capturedAt;
        Duration = duration;
        Freshness = freshness;
        RootDiagnostics = Array.AsReadOnly((rootDiagnostics ?? Array.Empty<ScanRootDiagnostic>()).ToArray());
    }

    public IReadOnlyList<ProjectModel> Projects { get; }

    /// <summary>The DevDesk checkout as its own workspace; null when not found.</summary>
    public ProjectModel? Self { get; }

    /// <summary>Template repositories discovered during the scan (read-only data).</summary>
    public IReadOnlyList<TemplateDescriptor> Templates { get; }

    public DateTimeOffset CapturedAt { get; }

    public TimeSpan Duration { get; }

    public CatalogFreshness Freshness { get; }

    public IReadOnlyList<ScanRootDiagnostic> RootDiagnostics { get; }

    public bool IsEmpty => Projects.Count == 0 && Self is null && Templates.Count == 0;

    public int CleanCount => Count(p => p.RemoteOk && !p.Dirty && !p.BehindUpstream);

    public int PendingDetailsCount => Count(p => p.DetailsPending);

    public int AttentionCount => Count(p => !p.DetailsPending && !(p.RemoteOk && !p.Dirty && !p.BehindUpstream));

    public int RemoteCount => Count(p => !p.DetailsPending && p.HasRemote);

    private int Count(Func<ProjectModel, bool> predicate)
    {
        var count = 0;
        foreach (var project in Projects)
        {
            if (predicate(project))
            {
                count++;
            }
        }

        return count;
    }
}

public enum CatalogFreshness
{
    Unscanned,
    Cached,
    Partial,
    Fresh,
    Stale,
}

/// <summary>
/// The single source of project state for the whole app (roadmap D9/S1).
/// <para>
/// One scan at a time: a refresh requested while a scan is running joins the
/// in-flight task instead of starting a second walk of the disk. That is what
/// makes Dashboard → DevDesk → Dashboard free: the second visit reads the
/// existing snapshot, and even a refresh click during the first scan joins it
/// rather than doubling the work.
/// </para>
/// <para>
/// A schema-versioned disk cache may seed the snapshot as cached data. That
/// cache never counts as a fresh scan and the first catalog visit revalidates it.
/// </para>
/// </summary>
public sealed class ProjectCatalog
{
    private static readonly TimeSpan PartialSnapshotNotificationInterval = TimeSpan.FromMilliseconds(50);

    private readonly IScanEngine _engine;
    private readonly TimeProvider _time;
    private readonly PerfCounters _counters;
    private readonly ScanCacheStore? _cache;
    private readonly object _gate = new();

    private CatalogSnapshot _snapshot;
    private Task<CatalogSnapshot>? _inFlight;
    private CancellationTokenSource? _activeCancellation;
    private int _scanCount;
    private bool _hasFreshSnapshot;
    private long _lastPartialNotification;
    private List<ProjectModel>? _partialProjects;
    private CatalogSnapshot? _partialBaseSnapshot;

    public ProjectCatalog(
        IScanEngine engine,
        TimeProvider? time = null,
        PerfCounters? counters = null,
        ScanCacheStore? cache = null,
        IReadOnlyList<ScanRootSettings>? cacheRoots = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _time = time ?? TimeProvider.System;
        _counters = counters ?? PerfCounters.Current;
        _cache = cache;
        _snapshot = cache?.Load(cacheRoots ?? new[] { new ScanRootSettings { Path = _engine.ScanRoot } })
            ?? CatalogSnapshot.Empty;
    }

    public ProjectCatalog()
        : this(new ScanEngine())
    {
    }

    /// <summary>Raised on the thread that finished the scan, never on the UI thread.</summary>
    public event EventHandler<CatalogSnapshot>? Changed;

    public CatalogSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public bool IsRefreshing
    {
        get
        {
            lock (_gate)
            {
                return _inFlight is not null;
            }
        }
    }

    /// <summary>Number of scans actually performed (joins do not count).</summary>
    public int ScanCount => Volatile.Read(ref _scanCount);

    public string ScanRoot => _engine.ScanRoot;

    public bool CancelCurrentScan()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _activeCancellation;
        }

        if (cancellation is null)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reuses the current snapshot when a scan has ever completed, otherwise
    /// scans. This is the call pages make when they appear.
    /// <para>
    /// "Never scanned" is <see cref="CatalogSnapshot.CapturedAt"/> being
    /// unset, not the project list being empty: an ecosystem with no projects
    /// is a valid result and must not make every page visit rescan the disk.
    /// </para>
    /// </summary>
    public Task<CatalogSnapshot> GetOrRefreshAsync(
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        var current = Snapshot;
        return !_hasFreshSnapshot
            ? RefreshAsync(cancellationToken, progress)
            : Task.FromResult(current);
    }

    /// <summary>Publish fresh local state after a write without walking every repository.</summary>
    public bool UpdateProject(ProjectModel updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        CatalogSnapshot next;
        lock (_gate)
        {
            var projectFound = false;
            var projects = new List<ProjectModel>(_snapshot.Projects.Count);
            foreach (var project in _snapshot.Projects)
            {
                if (PathsEqual(project.Path, updated.Path))
                {
                    projects.Add(updated);
                    projectFound = true;
                }
                else
                {
                    projects.Add(project);
                }
            }

            var self = _snapshot.Self;
            if (self is not null && PathsEqual(self.Path, updated.Path))
            {
                self = updated;
                projectFound = true;
            }

            if (!projectFound) return false;
            next = new CatalogSnapshot(
                projects,
                self,
                _snapshot.Templates,
                _snapshot.CapturedAt,
                _snapshot.Duration,
                _snapshot.Freshness,
                _snapshot.RootDiagnostics);
            _snapshot = next;
        }

        Changed?.Invoke(this, next);
        return true;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scans, or joins the scan already running. The returned task yields the
    /// same snapshot every caller that joined it will see.
    /// </summary>
    public Task<CatalogSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        TaskCompletionSource<CatalogSnapshot> completion;
        CancellationToken scanToken;

        lock (_gate)
        {
            if (_inFlight is not null)
            {
                return _inFlight;
            }

            completion = new TaskCompletionSource<CatalogSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
            var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = scanCancellation;
            scanToken = scanCancellation.Token;
        }

        _ = RunScanAsync(completion, scanToken, progress);
        return completion.Task;
    }

    public Task<CatalogSnapshot> RefreshRootAsync(
        ScanRootSettings root,
        IReadOnlyList<ScanRootSettings>? configuredRoots = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (_engine is not IScanRootEngine rootEngine)
        {
            return RefreshAsync(cancellationToken);
        }

        TaskCompletionSource<CatalogSnapshot> completion;
        CatalogSnapshot startingSnapshot;
        CancellationToken scanToken;
        lock (_gate)
        {
            if (_inFlight is not null)
            {
                return _inFlight;
            }

            if (!_hasFreshSnapshot)
            {
                return RefreshAsync(cancellationToken);
            }

            startingSnapshot = _snapshot;
            completion = new TaskCompletionSource<CatalogSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
            var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = scanCancellation;
            scanToken = scanCancellation.Token;
        }

        _ = RunRootScanAsync(
            rootEngine,
            root,
            configuredRoots,
            startingSnapshot,
            completion,
            scanToken);
        return completion.Task;
    }

    public Task<CatalogSnapshot> RefreshCheckoutAsync(
        ScanRootSettings root,
        string checkoutPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutPath);
        if (_engine is not IScanCheckoutEngine checkoutEngine)
        {
            return RefreshRootAsync(root, cancellationToken: cancellationToken);
        }

        TaskCompletionSource<CatalogSnapshot> completion;
        CatalogSnapshot startingSnapshot;
        CancellationToken scanToken;
        lock (_gate)
        {
            if (_inFlight is not null)
            {
                return _inFlight;
            }

            if (!_hasFreshSnapshot
                || !_snapshot.Projects.Any(project => PathsEqual(project.Path, checkoutPath)))
            {
                return RefreshRootAsync(root, cancellationToken: cancellationToken);
            }

            startingSnapshot = _snapshot;
            completion = new TaskCompletionSource<CatalogSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
            var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = scanCancellation;
            scanToken = scanCancellation.Token;
        }

        _ = RunCheckoutScanAsync(
            checkoutEngine,
            root,
            checkoutPath,
            startingSnapshot,
            completion,
            scanToken);
        return completion.Task;
    }

    private async Task RunCheckoutScanAsync(
        IScanCheckoutEngine checkoutEngine,
        ScanRootSettings root,
        string checkoutPath,
        CatalogSnapshot startingSnapshot,
        TaskCompletionSource<CatalogSnapshot> completion,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scanCount);
        var started = _time.GetTimestamp();
        try
        {
            var project = await Task.Run(
                    () => checkoutEngine.ScanCheckoutAsync(root, checkoutPath, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed = _time.GetElapsedTime(started, _time.GetTimestamp());
            var projects = new List<ProjectModel>(startingSnapshot.Projects.Count);
            var replacementAdded = false;
            foreach (var existing in startingSnapshot.Projects)
            {
                if (!PathsEqual(existing.Path, checkoutPath))
                {
                    projects.Add(existing);
                }
                else if (project is not null && !replacementAdded)
                {
                    projects.Add(project);
                    replacementAdded = true;
                }
            }

            if (project is not null && !replacementAdded)
            {
                projects.Add(project);
            }

            var snapshot = new CatalogSnapshot(
                projects,
                startingSnapshot.Self,
                startingSnapshot.Templates,
                DateTimeOffset.Now,
                elapsed,
                startingSnapshot.RootDiagnostics.Count > 0
                    ? CatalogFreshness.Partial
                    : CatalogFreshness.Fresh,
                startingSnapshot.RootDiagnostics);
            lock (_gate)
            {
                _snapshot = snapshot;
                _hasFreshSnapshot = true;
                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            _cache?.Save(snapshot);
            _counters.RecordScan(elapsed, snapshot.Projects.Count, PerfOutcome.Ok);
            Changed?.Invoke(this, snapshot);
            completion.TrySetResult(snapshot);
        }
        catch (OperationCanceledException ex)
        {
            lock (_gate)
            {
                _snapshot = startingSnapshot;
                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            _counters.RecordScan(
                _time.GetElapsedTime(started, _time.GetTimestamp()),
                0,
                PerfOutcome.Skipped);
            Changed?.Invoke(this, startingSnapshot);
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            var stale = new CatalogSnapshot(
                startingSnapshot.Projects,
                startingSnapshot.Self,
                startingSnapshot.Templates,
                startingSnapshot.CapturedAt,
                startingSnapshot.Duration,
                CatalogFreshness.Stale,
                startingSnapshot.RootDiagnostics.Append(new ScanRootDiagnostic(
                    root.Path,
                    Path.GetRelativePath(root.Path, checkoutPath),
                    ex.Message)));
            lock (_gate)
            {
                _snapshot = stale;
                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            _counters.RecordScan(
                _time.GetElapsedTime(started, _time.GetTimestamp()),
                -1,
                PerfOutcome.Failed);
            LoggingService.Log.Error(ex, "Project checkout refresh failed for {Checkout}", checkoutPath);
            Changed?.Invoke(this, stale);
            completion.TrySetException(ex);
        }
    }

    private async Task RunRootScanAsync(
        IScanRootEngine rootEngine,
        ScanRootSettings root,
        IReadOnlyList<ScanRootSettings>? configuredRoots,
        CatalogSnapshot startingSnapshot,
        TaskCompletionSource<CatalogSnapshot> completion,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scanCount);
        var started = _time.GetTimestamp();
        try
        {
            var progress = new InlineProgress<ScanProgress>(update =>
            {
                if (update.Project is not null)
                {
                    PublishPartial(startingSnapshot, update.Project);
                }
            });
            var result = await Task.Run(
                    () => rootEngine.ScanRootAsync(root, cancellationToken, progress),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed = _time.GetElapsedTime(started, _time.GetTimestamp());
            var diagnostics = startingSnapshot.RootDiagnostics
                .Where(issue => !string.Equals(
                    issue.RootPath,
                    root.Path,
                    StringComparison.OrdinalIgnoreCase))
                .Concat(result.RootDiagnostics)
                .ToArray();
            var projects = startingSnapshot.Projects
                .Where(project => !BelongsToRoot(project, root.Path)
                    || result.RootDiagnostics.Any(issue => BelongsToRoot(project, issue.RootPath)))
                .Concat(result.Projects)
                .GroupBy(project => PathHygiene.Canonicalize(project.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
            if (configuredRoots is { Count: > 0 })
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < configuredRoots.Count; index++)
                {
                    order.TryAdd(PathHygiene.Canonicalize(configuredRoots[index].Path), index);
                }

                projects = projects
                    .OrderBy(project => order.TryGetValue(project.ScanRootPath, out var index) ? index : int.MaxValue)
                    .ThenBy(project => project.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var templates = startingSnapshot.Templates
                .Where(template => !PathHygiene.IsUnder(template.FullPath, root.Path)
                    || result.RootDiagnostics.Any(issue => PathHygiene.IsUnder(template.FullPath, issue.RootPath)))
                .Concat(result.Templates)
                .ToArray();
            var self = result.Self is not null && PathHygiene.IsUnder(result.Self.Path, root.Path)
                ? result.Self
                : startingSnapshot.Self;
            var snapshot = new CatalogSnapshot(
                projects,
                self,
                templates,
                DateTimeOffset.Now,
                elapsed,
                diagnostics.Length > 0 ? CatalogFreshness.Partial : CatalogFreshness.Fresh,
                diagnostics);
            lock (_gate)
            {
                _snapshot = snapshot;
                _hasFreshSnapshot = true;
                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            _cache?.Save(snapshot);
            _counters.RecordScan(elapsed, snapshot.Projects.Count, PerfOutcome.Ok);
            Changed?.Invoke(this, snapshot);
            completion.TrySetResult(snapshot);
        }
        catch (OperationCanceledException ex)
        {
            lock (_gate)
            {
                _snapshot = startingSnapshot;
                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            _counters.RecordScan(
                _time.GetElapsedTime(started, _time.GetTimestamp()),
                0,
                PerfOutcome.Skipped);
            Changed?.Invoke(this, startingSnapshot);
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            CatalogSnapshot stale;
            lock (_gate)
            {
                stale = new CatalogSnapshot(
                    startingSnapshot.Projects,
                    startingSnapshot.Self,
                    startingSnapshot.Templates,
                    startingSnapshot.CapturedAt,
                    startingSnapshot.Duration,
                    CatalogFreshness.Stale,
                    startingSnapshot.RootDiagnostics
                        .Where(issue => !string.Equals(
                            issue.RootPath,
                            root.Path,
                            StringComparison.OrdinalIgnoreCase))
                        .Append(new ScanRootDiagnostic(root.Path, string.Empty, ex.Message)));
                _snapshot = stale;
                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            _counters.RecordScan(
                _time.GetElapsedTime(started, _time.GetTimestamp()),
                -1,
                PerfOutcome.Failed);
            LoggingService.Log.Error(ex, "Project root refresh failed for {Root}", root.Path);
            Changed?.Invoke(this, stale);
            completion.TrySetException(ex);
        }
    }

    private static bool BelongsToRoot(ProjectModel project, string root)
    {
        try
        {
            return (!string.IsNullOrWhiteSpace(project.ScanRootPath)
                    && string.Equals(
                        PathHygiene.Canonicalize(project.ScanRootPath),
                        PathHygiene.Canonicalize(root),
                        StringComparison.OrdinalIgnoreCase))
                || PathHygiene.IsUnder(project.Path, root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ProjectModel> RetainFailedRootProjects(
        IReadOnlyList<ProjectModel> previous,
        IReadOnlyList<ProjectModel> refreshed,
        IReadOnlyList<ScanRootDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0 || previous.Count == 0)
        {
            return refreshed;
        }

        var merged = refreshed.ToList();
        foreach (var project in previous)
        {
            if (diagnostics.Any(issue => BelongsToRoot(project, issue.RootPath))
                && !merged.Any(item => PathsEqual(item.Path, project.Path)))
            {
                merged.Add(project);
            }
        }

        return merged;
    }

    private static IReadOnlyList<TemplateDescriptor> RetainFailedRootTemplates(
        IReadOnlyList<TemplateDescriptor> previous,
        IReadOnlyList<TemplateDescriptor> refreshed,
        IReadOnlyList<ScanRootDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0 || previous.Count == 0)
        {
            return refreshed;
        }

        var merged = refreshed.ToList();
        foreach (var template in previous)
        {
            if (diagnostics.Any(issue => PathHygiene.IsUnder(template.FullPath, issue.RootPath))
                && !merged.Any(item => PathsEqual(item.FullPath, template.FullPath)))
            {
                merged.Add(template);
            }
        }

        return merged;
    }

    private async Task RunScanAsync(
        TaskCompletionSource<CatalogSnapshot> completion,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress)
    {
        Interlocked.Increment(ref _scanCount);
        var started = _time.GetTimestamp();
        CatalogSnapshot startingSnapshot;
        lock (_gate)
        {
            startingSnapshot = _snapshot;
        }

        try
        {
            // Task.Run: the walk over the disk is synchronous work, and a page
            // must never have its UI thread inside a Directory.GetDirectories.
            var scanProgress = new InlineProgress<ScanProgress>(update =>
            {
                if (update.Project is not null)
                {
                    PublishPartial(startingSnapshot, update.Project);
                }

                progress?.Report(update);
            });
            var result = await Task
                .Run(() => _engine.ScanAsync(cancellationToken, scanProgress), cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // The duration is measured with the injected clock, not a Stopwatch:
            // a test can hand this a fixed clock instead of racing the wall.
            var elapsed = _time.GetElapsedTime(started, _time.GetTimestamp());

            var snapshot = new CatalogSnapshot(
                RetainFailedRootProjects(startingSnapshot.Projects, result.Projects, result.RootDiagnostics),
                result.Self ?? startingSnapshot.Self,
                RetainFailedRootTemplates(startingSnapshot.Templates, result.Templates, result.RootDiagnostics),
                DateTimeOffset.Now,
                elapsed,
                result.RootDiagnostics.Count > 0 ? CatalogFreshness.Partial : CatalogFreshness.Fresh,
                result.RootDiagnostics);

            lock (_gate)
            {
                _snapshot = snapshot;
                _hasFreshSnapshot = true;
                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            _cache?.Save(snapshot);
            _counters.RecordScan(elapsed, snapshot.Projects.Count, PerfOutcome.Ok);

            // For a cache miss, the fresh catalog result is the first snapshot
            // available to paint; cache hydration marks this when AppServices
            // constructs the catalog. Keep it independent of the restored route.
            StartupTimeline.Current.Mark(StartupTimeline.FirstSnapshot);

            var scan = Volatile.Read(ref _scanCount);
            LoggingService.Log.Information(
                "Catalog scan #{Scan} finished in {Elapsed} ms: {Projects} project(s), {Templates} template(s), self {Self}",
                scan,
                (long)elapsed.TotalMilliseconds,
                snapshot.Projects.Count,
                snapshot.Templates.Count,
                snapshot.Self?.Name ?? "not found");

            Changed?.Invoke(this, snapshot);
            completion.TrySetResult(snapshot);
        }
        catch (OperationCanceledException ex)
        {
            CatalogSnapshot? restored = null;
            lock (_gate)
            {
                if (_snapshot.Freshness == CatalogFreshness.Partial)
                {
                    _snapshot = startingSnapshot;
                    restored = startingSnapshot;
                }

                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            // A cancellation is a scan that did not finish: it is counted, but it
            // must not become "the last scan" and pretend to have a duration.
            _counters.RecordScan(
                _time.GetElapsedTime(started, _time.GetTimestamp()),
                0,
                PerfOutcome.Skipped);

            LoggingService.Log.Information("Catalog scan cancelled");
            if (restored is not null)
            {
                Changed?.Invoke(this, restored);
            }

            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            CatalogSnapshot? stale = null;
            lock (_gate)
            {
                if (startingSnapshot.CapturedAt != DateTimeOffset.MinValue
                    && startingSnapshot.Freshness != CatalogFreshness.Stale)
                {
                    stale = new CatalogSnapshot(
                        startingSnapshot.Projects,
                        startingSnapshot.Self,
                        startingSnapshot.Templates,
                        startingSnapshot.CapturedAt,
                        startingSnapshot.Duration,
                        CatalogFreshness.Stale);
                    _snapshot = stale;
                }
                else
                {
                    _snapshot = startingSnapshot;
                    if (startingSnapshot.Freshness == CatalogFreshness.Partial)
                    {
                        _snapshot = CatalogSnapshot.Empty;
                    }
                }

                _inFlight = null;
                ClearPartialProgress();
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }

            // Projects is -1 on purpose: a failed scan found nothing, and the card
            // should not print "0 repos" as if that were a real answer.
            _counters.RecordScan(
                _time.GetElapsedTime(started, _time.GetTimestamp()),
                -1,
                PerfOutcome.Failed);

            LoggingService.Log.Error(ex, "Catalog scan failed");
            if (stale is not null)
            {
                Changed?.Invoke(this, stale);
            }

            completion.TrySetException(ex);
        }
    }

    private void PublishPartial(CatalogSnapshot startingSnapshot, ProjectModel project)
    {
        CatalogSnapshot? partial = null;
        lock (_gate)
        {
            if (_inFlight is null || _snapshot.Freshness == CatalogFreshness.Stale)
            {
                return;
            }

            if (!ReferenceEquals(_partialBaseSnapshot, startingSnapshot))
            {
                _partialBaseSnapshot = startingSnapshot;
                _partialProjects = new List<ProjectModel>(startingSnapshot.Projects);
                _lastPartialNotification = 0;
            }

            var projects = _partialProjects!;
            var existing = projects.FindIndex(item => PathsEqual(item.Path, project.Path));
            if (existing >= 0)
            {
                projects[existing] = project;
            }
            else
            {
                projects.Add(project);
            }

            var now = _time.GetTimestamp();
            var notify = _snapshot.Freshness != CatalogFreshness.Partial
                || _time.GetElapsedTime(_lastPartialNotification, now) >= PartialSnapshotNotificationInterval;
            if (notify)
            {
                _lastPartialNotification = now;
                partial = new CatalogSnapshot(
                    projects,
                    startingSnapshot.Self,
                    startingSnapshot.Templates,
                    startingSnapshot.CapturedAt,
                    startingSnapshot.Duration,
                    CatalogFreshness.Partial);
                _snapshot = partial;
            }
        }

        if (partial is not null)
        {
            Changed?.Invoke(this, partial);
        }
    }

    private void ClearPartialProgress()
    {
        _partialProjects = null;
        _partialBaseSnapshot = null;
        _lastPartialNotification = 0;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
