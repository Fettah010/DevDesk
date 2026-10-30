using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        DateTimeOffset.MinValue,
        TimeSpan.Zero);

    public CatalogSnapshot(
        IEnumerable<ProjectModel> projects,
        ProjectModel? self,
        DateTimeOffset capturedAt,
        TimeSpan duration)
    {
        Projects = new ReadOnlyCollection<ProjectModel>(new List<ProjectModel>(projects));
        Self = self;
        CapturedAt = capturedAt;
        Duration = duration;
    }

    public IReadOnlyList<ProjectModel> Projects { get; }

    /// <summary>The DevDesk checkout as its own workspace; null when not found.</summary>
    public ProjectModel? Self { get; }

    public DateTimeOffset CapturedAt { get; }

    public TimeSpan Duration { get; }

    public bool IsEmpty => Projects.Count == 0 && Self is null;

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
    private readonly object _gate = new();

    private CatalogSnapshot _snapshot = CatalogSnapshot.Empty;
    private Task<CatalogSnapshot>? _inFlight;
    private int _scanCount;

    public ProjectCatalog(IScanEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
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
    public Task<CatalogSnapshot> GetOrRefreshAsync(CancellationToken cancellationToken = default)
    {
        var current = Snapshot;
        return current.CapturedAt == DateTimeOffset.MinValue
            ? RefreshAsync(cancellationToken)
            : Task.FromResult(current);
    }

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
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _engine
                .ScanAsync(cancellationToken, progress)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            stopwatch.Stop();

            var snapshot = new CatalogSnapshot(
                result.Projects,
                result.Self,
                DateTimeOffset.Now,
                stopwatch.Elapsed);

            lock (_gate)
            {
                _snapshot = snapshot;
                _inFlight = null;
            }

            var scan = Volatile.Read(ref _scanCount);
            LoggingService.Log.Information(
                "Catalog scan #{Scan} finished in {Elapsed} ms: {Projects} project(s), self {Self}",
                scan,
                (long)stopwatch.Elapsed.TotalMilliseconds,
                snapshot.Projects.Count,
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

            LoggingService.Log.Information("Catalog scan cancelled");
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _inFlight = null;
            }

            LoggingService.Log.Error(ex, "Catalog scan failed");
            completion.TrySetException(ex);
        }
    }
}
