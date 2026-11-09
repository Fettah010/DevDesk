using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DevDesk.Services;

/// <summary>How a timed operation ended. A skipped operation is counted, never timed.</summary>
public enum PerfOutcome
{
    Ok,
    Failed,

    /// <summary>Cancelled, or not attempted (for example: updates when not installed).</summary>
    Skipped,
}

/// <summary>
/// The budgets a measured number is compared against, copied from the roadmap
/// (§6.3 and success metrics M1/M2/M3/M5) rather than invented here. Keeping them
/// beside the counters means the Diagnostics card and the tests cannot disagree
/// about what "fast enough" means.
/// </summary>
public static class PerfBudgets
{
    /// <summary>M2 — cached window appearance, validated by desktop UI automation.</summary>
    public static TimeSpan StartupToWindow { get; } = TimeSpan.FromMilliseconds(400);

    /// <summary>M1 — warm start until the first project card has a realized layout container.</summary>
    public static TimeSpan StartupTotal { get; } = TimeSpan.FromMilliseconds(700);

    /// <summary>M3 — a full scan of the eight real repositories in the ecosystem.</summary>
    public static TimeSpan Scan { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>§6.3 — one settings/cache write, off the UI thread.</summary>
    public static TimeSpan SettingsWrite { get; } = TimeSpan.FromMilliseconds(20);

    /// <summary>M5 — memory with a hundred repositories loaded.</summary>
    public const long WorkingSetBytes = 300L * 1024 * 1024;

    /// <summary>M6 — observed UI dispatcher delay beyond the heartbeat cadence.</summary>
    public static TimeSpan UiDispatchDelay { get; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Process-wide counters plus the last measured duration of each timed operation
/// (roadmap F18). The Diagnostics "Performance" card renders these, and the log
/// has the matching INF lines, so the number on screen can be checked against the
/// file.
/// <para>
/// Everything is <see cref="Interlocked"/>: scans finish on a worker thread,
/// update checks on a network thread and the last duration read happens on the UI
/// thread, and a counter is not worth a lock on the scan path. Reading a single
/// value and writing a single value are therefore atomic; a <em>set</em> of
/// related values read separately can belong to two different operations, which
/// is harmless for a diagnostic card that is refreshed on demand.
/// </para>
/// <para>
/// Log events evicted by the ring are deliberately <b>not</b> counted here:
/// <see cref="LogRingBufferSink.Dropped"/> owns that number, and a second counter
/// would be a second truth.
/// </para>
/// </summary>
public sealed class PerfCounters
{
    public static PerfCounters Current { get; } = new();

    /// <summary>Sentinel for "this operation has not completed in this session".</summary>
    private const long NotMeasured = -1;

    /// <summary>
    /// UI operations at or above this are logged individually. Well below the
    /// 50 ms M6 budget on purpose: two 30 ms operations in one dispatcher window
    /// are what turns into a violation, and neither would be visible otherwise.
    /// </summary>
    private static readonly TimeSpan SlowUiOperationThreshold = TimeSpan.FromMilliseconds(15);

    private long _scans;
    private long _updateChecks;
    private long _settingsWrites;
    private long _builds;
    private long _failures;

    private long _lastScanMs = NotMeasured;
    private long _lastScanProjects = NotMeasured;
    private long _lastUpdateCheckMs = NotMeasured;
    private long _lastSettingsWriteMs = NotMeasured;
    private long _lastBuildMs = NotMeasured;
    private long _maxUiDispatchDelayTicks;

    private readonly TimeProvider _time;

    /// <summary>Named UI-thread operations currently on the dispatcher call stack.</summary>
    /// <remarks>Only the UI thread pushes/pops this, so no lock is needed.</remarks>
    private readonly Stack<(string Name, long StartedAt)> _uiOperations = new();

    /// <summary>
    /// The longest UI operation that finished since the heartbeat last sampled.
    /// A dispatcher delay is observed on the <em>next</em> tick, by which time the
    /// blocking operation has already returned, so the live stack would name the
    /// heartbeat itself. Recording completed work is what makes the name real.
    /// </summary>
    private TimeSpan _longestOperationSinceSample = TimeSpan.Zero;
    private string? _longestOperationSinceSampleName;
    private int _longestOperationSinceSampleDepth;

    public PerfCounters(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Scans started, including the ones that failed or were cancelled.</summary>
    public long Scans => Interlocked.Read(ref _scans);

    public long UpdateChecks => Interlocked.Read(ref _updateChecks);

    public long SettingsWrites => Interlocked.Read(ref _settingsWrites);

    /// <summary>Build/run/publish/test processes that finished, cancelled ones included.</summary>
    public long Builds => Interlocked.Read(ref _builds);

    /// <summary>Operations that ended badly. Must stay 0 on a healthy run.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>The last completed scan, or null when none has finished yet.</summary>
    public TimeSpan? LastScan => ToDuration(Interlocked.Read(ref _lastScanMs));

    /// <summary>How many repositories that scan found; null when none has finished.</summary>
    public int? LastScanProjects
    {
        get
        {
            var value = Interlocked.Read(ref _lastScanProjects);
            return value < 0 ? null : (int)value;
        }
    }

    public TimeSpan? LastUpdateCheck => ToDuration(Interlocked.Read(ref _lastUpdateCheckMs));

    public TimeSpan? LastSettingsWrite => ToDuration(Interlocked.Read(ref _lastSettingsWriteMs));

    /// <summary>The last finished build-family process, or null when none has run.</summary>
    public TimeSpan? LastBuild => ToDuration(Interlocked.Read(ref _lastBuildMs));

    /// <summary>Largest observed UI dispatcher delay, or null until sampled.</summary>
    public TimeSpan? MaxUiDispatchDelay
    {
        get
        {
            var value = Interlocked.Read(ref _maxUiDispatchDelayTicks);
            return value == 0 ? null : TimeSpan.FromTicks(value);
        }
    }

    /// <summary>
    /// Name of the UI-thread operation that was active when <see cref="MaxUiDispatchDelay"/>
    /// was observed, or null when no operation was on the stack. Helps attribute
    /// M6 violations to a specific page or service instead of an anonymous tick.
    /// </summary>
    public string? MaxUiDispatchDelayDuring { get; private set; }

    /// <summary>
    /// How long that attributed operation held the UI thread, when the heartbeat
    /// was able to see a completed operation. Null when nothing ran between the
    /// two ticks that produced the delay.
    /// </summary>
    public TimeSpan? MaxUiDispatchDelayOperationElapsed { get; private set; }

    /// <summary>Resident memory of this process (M5's 300 MB is the 100-repo target).</summary>
    public long WorkingSetBytes => Environment.WorkingSet;

    public void RecordScan(TimeSpan elapsed, int projects, PerfOutcome outcome)
    {
        Interlocked.Increment(ref _scans);
        CountIfFailed(outcome);

        if (outcome == PerfOutcome.Skipped)
        {
            return;
        }

        Interlocked.Exchange(ref _lastScanMs, ToMilliseconds(elapsed));
        Interlocked.Exchange(ref _lastScanProjects, projects);
    }

    public void RecordUpdateCheck(TimeSpan elapsed, PerfOutcome outcome)
    {
        Interlocked.Increment(ref _updateChecks);
        CountIfFailed(outcome);

        if (outcome != PerfOutcome.Skipped)
        {
            Interlocked.Exchange(ref _lastUpdateCheckMs, ToMilliseconds(elapsed));
        }
    }

    public void RecordSettingsWrite(TimeSpan elapsed, PerfOutcome outcome)
    {
        Interlocked.Increment(ref _settingsWrites);
        CountIfFailed(outcome);

        if (outcome != PerfOutcome.Skipped)
        {
            Interlocked.Exchange(ref _lastSettingsWriteMs, ToMilliseconds(elapsed));
        }
    }

    /// <summary>Counts a failure that has no duration of its own.</summary>
    public void RecordFailure() => Interlocked.Increment(ref _failures);

    /// <summary>Retains the maximum delay observed by the UI dispatcher heartbeat.</summary>
    /// <param name="delay">How far past the heartbeat cadence this sample arrived.</param>
    /// <param name="duringOperation">
    /// The operation the caller attributes the delay to. The heartbeat passes the
    /// longest operation that completed since the previous sample; when omitted
    /// the currently active operation is used.
    /// </param>
    /// <param name="operationElapsed">How long that operation held the UI thread, if known.</param>
    public void RecordUiDispatchDelay(
        TimeSpan delay,
        string? duringOperation = null,
        TimeSpan? operationElapsed = null)
    {
        var ticks = Math.Max(0, delay.Ticks);
        var current = Interlocked.Read(ref _maxUiDispatchDelayTicks);
        while (ticks > current)
        {
            var observed = Interlocked.CompareExchange(ref _maxUiDispatchDelayTicks, ticks, current);
            if (observed == current)
            {
                // Won the CAS: record which UI-thread operation was active.
                MaxUiDispatchDelayDuring = duringOperation
                    ?? (_uiOperations.Count > 0 ? _uiOperations.Peek().Name : null);
                MaxUiDispatchDelayOperationElapsed = operationElapsed;
                LoggingService.Log.Warning(
                    "Startup: UI dispatch delay {Delay} ms during {Operation} ({OperationMs} ms on the UI thread)",
                    (long)delay.TotalMilliseconds,
                    MaxUiDispatchDelayDuring ?? "<idle>",
                    operationElapsed is { } elapsed ? (long)elapsed.TotalMilliseconds : -1);
                return;
            }

            current = observed;
        }
    }

    /// <summary>
    /// Hands the heartbeat the longest UI operation completed since the previous
    /// sample and resets the record, so each delay is attributed to the work that
    /// occupied that specific window rather than to unrelated earlier work.
    /// </summary>
    public (string Name, TimeSpan Elapsed)? TakeLongestUiOperationSinceLastSample()
    {
        var name = _longestOperationSinceSampleName;
        var elapsed = _longestOperationSinceSample;
        _longestOperationSinceSample = TimeSpan.Zero;
        _longestOperationSinceSampleName = null;
        _longestOperationSinceSampleDepth = 0;
        return name is null ? null : (name, elapsed);
    }

    /// <summary>
    /// Begins a named UI-thread operation so that dispatcher delays sampled by
    /// <see cref="UiThreadHeartbeat"/> can be attributed to a specific page or
    /// service. Returns an <see cref="IDisposable"/>; dispose to end the operation.
    /// </summary>
    public IDisposable BeginUiOperation(string name)
    {
        _uiOperations.Push((name, _time.GetTimestamp()));
        return new UiOperationDisposable(this);
    }

    private void EndUiOperation()
    {
        if (_uiOperations.Count == 0)
        {
            return;
        }

        // Depth is taken before the pop, so the innermost scope has the largest
        // depth and wins a tie: naming "dashboard xaml initialize" is more useful
        // than naming the page-load scope that contains it.
        var depth = _uiOperations.Count;
        var (name, startedAt) = _uiOperations.Pop();
        var elapsed = _time.GetElapsedTime(startedAt, _time.GetTimestamp());
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        if (elapsed >= SlowUiOperationThreshold)
        {
            LoggingService.Log.Debug(
                "UI operation {Operation} took {Elapsed} ms",
                name,
                (long)elapsed.TotalMilliseconds);
        }

        if (depth > _longestOperationSinceSampleDepth
            || (depth == _longestOperationSinceSampleDepth && elapsed > _longestOperationSinceSample))
        {
            _longestOperationSinceSample = elapsed;
            _longestOperationSinceSampleName = name;
            _longestOperationSinceSampleDepth = depth;
        }
    }

    private sealed class UiOperationDisposable : IDisposable
    {
        private readonly PerfCounters _counters;
        private bool _disposed;

        public UiOperationDisposable(PerfCounters counters) => _counters = counters;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _counters.EndUiOperation();
            }
        }
    }

    /// <summary>
    /// Records one build-family process. A cancelled build is counted but not
    /// timed: the card answers "how long does a build take here", and a build the
    /// user stopped after 300 ms says nothing about the tool.
    /// </summary>
    public void RecordBuild(TimeSpan elapsed, PerfOutcome outcome)
    {
        Interlocked.Increment(ref _builds);
        CountIfFailed(outcome);

        if (outcome != PerfOutcome.Skipped)
        {
            Interlocked.Exchange(ref _lastBuildMs, ToMilliseconds(elapsed));
        }
    }

    /// <summary>
    /// "380 ms", "1.24 s", "0.4 ms", "2m 05s" — invariant culture, so a machine
    /// with a comma decimal separator does not turn a table into a puzzle.
    /// </summary>
    public static string FormatDuration(TimeSpan? elapsed)
    {
        if (elapsed is not { } value)
        {
            return "—";
        }

        var milliseconds = Math.Max(0, value.TotalMilliseconds);

        if (milliseconds < 1)
        {
            return Format("{0:0.0} ms", milliseconds);
        }

        if (milliseconds < 1000)
        {
            return Format("{0:0} ms", milliseconds);
        }

        if (milliseconds < 60_000)
        {
            return Format("{0:0.00} s", milliseconds / 1000);
        }

        return Format(
            "{0:0}m {1:00}s",
            Math.Floor(milliseconds / 60_000),
            Math.Floor(milliseconds % 60_000 / 1000));
    }

    public static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);

        if (value < 1024)
        {
            return Format("{0} B", value);
        }

        if (value < 1024L * 1024)
        {
            return Format("{0:0} KB", value / 1024.0);
        }

        if (value < 1024L * 1024 * 1024)
        {
            return Format("{0:0} MB", value / (1024.0 * 1024));
        }

        return Format("{0:0.0} GB", value / (1024.0 * 1024 * 1024));
    }

    private void CountIfFailed(PerfOutcome outcome)
    {
        if (outcome == PerfOutcome.Failed)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    private static TimeSpan? ToDuration(long milliseconds) =>
        milliseconds < 0 ? null : TimeSpan.FromMilliseconds(milliseconds);

    private static long ToMilliseconds(TimeSpan elapsed) =>
        (long)Math.Round(Math.Max(0, elapsed.TotalMilliseconds));

    private static string Format(string format, params object[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, format, arguments);
}
