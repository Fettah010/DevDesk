using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
        TimeSpan.Zero);

    public CatalogSnapshot(
        IEnumerable<ProjectModel> projects,
        ProjectModel? self,
        IEnumerable<TemplateDescriptor>? templates = null,
        DateTimeOffset capturedAt = default,
        TimeSpan duration = default)
    {
        Projects = new ReadOnlyCollection<ProjectModel>(new List<ProjectModel>(projects));
        Self = self;
        Templates = new ReadOnlyCollection<TemplateDescriptor>(new List<TemplateDescriptor>(templates ?? Array.Empty<TemplateDescriptor>()));
        CapturedAt = capturedAt;
        Duration = duration;
    }

    public IReadOnlyList<ProjectModel> Projects { get; }

    /// <summary>The DevDesk checkout as its own workspace; null when not found.</summary>
    public ProjectModel? Self { get; }

    /// <summary>Template repositories discovered during the scan (read-only data).</summary>
    public IReadOnlyList<TemplateDescriptor> Templates { get; }

    public DateTimeOffset CapturedAt { get; }

    public TimeSpan Duration { get; }

    public bool IsEmpty => Projects.Count == 0 && Self is null && Templates.Count == 0;

    public int CleanCount => Count(p => p.RemoteOk && !p.Dirty && !p.BehindUpstream);

    public int AttentionCount => Projects.Count - CleanCount;

    public int RemoteCount => Count(p => p.HasRemote);

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
/// Nothing is persisted (S1 option A); the snapshot lives for the process.
/// </para>
/// </summary>
public sealed class ProjectCatalog
{
    private readonly IScanEngine _engine;
    private readonly TimeProvider _time;
    private readonly PerfCounters _counters;
    private readonly object _gate = new();

    private CatalogSnapshot _snapshot = CatalogSnapshot.Empty;
    private Task<CatalogSnapshot>? _inFlight;
    private int _scanCount;

    public ProjectCatalog(IScanEngine engine, TimeProvider? time = null, PerfCounters? counters = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _time = time ?? TimeProvider.System;
        _counters = counters ?? PerfCounters.Current;
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
        return current.CapturedAt == DateTimeOffset.MinValue
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
            next = new CatalogSnapshot(projects, self, _snapshot.Templates, _snapshot.CapturedAt, _snapshot.Duration);
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

        lock (_gate)
        {
            if (_inFlight is not null)
            {
                return _inFlight;
            }

            completion = new TaskCompletionSource<CatalogSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
        }

        _ = RunScanAsync(completion, cancellationToken, progress);
        return completion.Task;
    }

    private async Task RunScanAsync(
        TaskCompletionSource<CatalogSnapshot> completion,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress)
    {
        Interlocked.Increment(ref _scanCount);
        var started = _time.GetTimestamp();

        try
        {
            // Task.Run: the walk over the disk is synchronous work, and a page
            // must never have its UI thread inside a Directory.GetDirectories.
            var result = await Task
                .Run(() => _engine.ScanAsync(cancellationToken, progress), cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // The duration is measured with the injected clock, not a Stopwatch:
            // a test can hand this a fixed clock instead of racing the wall.
            var elapsed = _time.GetElapsedTime(started, _time.GetTimestamp());

            var snapshot = new CatalogSnapshot(
                result.Projects,
                result.Self,
                result.Templates,
                DateTimeOffset.Now,
                elapsed);

            lock (_gate)
            {
                _snapshot = snapshot;
                _inFlight = null;
            }

            _counters.RecordScan(elapsed, snapshot.Projects.Count, PerfOutcome.Ok);

            // "Warm start" ends here: the moment a snapshot exists to paint. It is
            // taken in the catalog rather than in a page so the total does not
            // depend on which route was restored (Diagnostics renders no cards).
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
            lock (_gate)
            {
                _inFlight = null;
            }

            // A cancellation is a scan that did not finish: it is counted, but it
            // must not become "the last scan" and pretend to have a duration.
            _counters.RecordScan(
                _time.GetElapsedTime(started, _time.GetTimestamp()),
                0,
                PerfOutcome.Skipped);

            LoggingService.Log.Information("Catalog scan cancelled");
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _inFlight = null;
            }

            // Projects is -1 on purpose: a failed scan found nothing, and the card
            // should not print "0 repos" as if that were a real answer.
            _counters.RecordScan(
                _time.GetElapsedTime(started, _time.GetTimestamp()),
                -1,
                PerfOutcome.Failed);

            LoggingService.Log.Error(ex, "Catalog scan failed");
            completion.TrySetException(ex);
        }
    }
}
