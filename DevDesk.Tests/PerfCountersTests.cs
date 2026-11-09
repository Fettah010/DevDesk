using System;
using System.Linq;
using System.Threading.Tasks;
using DevDesk.Services;
using DevDesk.ViewModels;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Instrumentation is only worth having if the numbers mean something: a mark is
/// taken once, counters survive concurrent writers, a duration reads the same on
/// screen as in the log, and the card compares against the roadmap's budgets.
/// <para>
/// Durations are driven by a manual <see cref="TimeProvider"/>, so none of these
/// tests races the wall or fails on a slow machine.
/// </para>
/// </summary>
public class PerfCountersTests
{
    [Fact]
    public void Timeline_MarksAreMonotonic()
    {
        var clock = new ManualTimeProvider();
        var timeline = new StartupTimeline(clock);

        timeline.Mark(StartupTimeline.ProcessStart);
        clock.Advance(TimeSpan.FromMilliseconds(120));
        timeline.Mark(StartupTimeline.LoggingReady);
        clock.Advance(TimeSpan.FromMilliseconds(260));
        timeline.Mark(StartupTimeline.WindowActivated);

        var marks = timeline.Marks;

        Assert.Equal(3, marks.Count);
        Assert.Equal(TimeSpan.Zero, marks[0].Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(120), marks[1].Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(380), marks[2].Elapsed);

        for (var index = 1; index < marks.Count; index++)
        {
            Assert.True(
                marks[index].Elapsed >= marks[index - 1].Elapsed,
                $"Mark '{marks[index].Name}' went backwards relative to '{marks[index - 1].Name}'.");
        }

        Assert.Equal(TimeSpan.FromMilliseconds(380), timeline.Total);
        Assert.Equal(TimeSpan.FromMilliseconds(120), timeline.Elapsed(StartupTimeline.LoggingReady));
        Assert.Null(timeline.Elapsed(StartupTimeline.FirstSnapshot));

        // A repeated mark keeps the first number, not the last re-navigation.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(380), timeline.Mark(StartupTimeline.WindowActivated));
        Assert.Equal(3, timeline.Marks.Count);
    }

    [Fact]
    public void PerfCounters_IncrementWithoutLocks()
    {
        var counters = new PerfCounters();
        const int iterations = 2000;

        // The point of Interlocked is that no update is lost even though the scan,
        // network and UI threads all write these at once.
        Parallel.For(0, iterations, _ =>
        {
            counters.RecordScan(TimeSpan.FromMilliseconds(12), 8, PerfOutcome.Ok);
            counters.RecordUpdateCheck(TimeSpan.FromMilliseconds(40), PerfOutcome.Ok);
            counters.RecordSettingsWrite(TimeSpan.FromMilliseconds(3), PerfOutcome.Ok);
            counters.RecordFailure();
        });

        Assert.Equal(iterations, counters.Scans);
        Assert.Equal(iterations, counters.UpdateChecks);
        Assert.Equal(iterations, counters.SettingsWrites);
        Assert.Equal(iterations, counters.Failures);

        counters.RecordScan(TimeSpan.FromMilliseconds(9), 0, PerfOutcome.Failed);

        Assert.Equal(iterations + 1, counters.Scans);
        Assert.Equal(iterations + 1, counters.Failures);
        Assert.Equal(TimeSpan.FromMilliseconds(9), counters.LastScan);
        Assert.Equal(0, counters.LastScanProjects);
    }

    [Fact]
    public void PerfCounters_SkippedIsCountedButNeverBecomesTheLastMeasurement()
    {
        var counters = new PerfCounters();
        counters.RecordUpdateCheck(TimeSpan.FromMilliseconds(50), PerfOutcome.Ok);
        counters.RecordUpdateCheck(TimeSpan.FromMilliseconds(1), PerfOutcome.Skipped);
        counters.RecordScan(TimeSpan.FromMilliseconds(300), 8, PerfOutcome.Ok);
        counters.RecordScan(TimeSpan.FromMilliseconds(2), 0, PerfOutcome.Skipped);

        // Attempts are visible; a skip is not a duration and does not erase the
        // last real number.
        Assert.Equal(2, counters.UpdateChecks);
        Assert.Equal(TimeSpan.FromMilliseconds(50), counters.LastUpdateCheck);
        Assert.Equal(2, counters.Scans);
        Assert.Equal(TimeSpan.FromMilliseconds(300), counters.LastScan);
        Assert.Equal(0, counters.Failures);
    }

    [Fact]
    public void UiThreadHeartbeat_RecordsDelayBeyondItsExpectedCadence()
    {
        var clock = new ManualTimeProvider();
        var counters = new PerfCounters();
        var heartbeat = new UiThreadHeartbeat(counters, clock);

        heartbeat.Sample();
        Assert.Null(counters.MaxUiDispatchDelay);

        clock.Advance(UiThreadHeartbeat.Interval);
        heartbeat.Sample();
        Assert.Null(counters.MaxUiDispatchDelay);

        clock.Advance(TimeSpan.FromMilliseconds(85));
        heartbeat.Sample();
        Assert.Equal(TimeSpan.FromMilliseconds(65), counters.MaxUiDispatchDelay);

        clock.Advance(TimeSpan.FromMilliseconds(25));
        heartbeat.Sample();
        Assert.Equal(TimeSpan.FromMilliseconds(65), counters.MaxUiDispatchDelay);
    }

    [Fact]
    public void PerfCounters_FormatsDurations()
    {
        Assert.Equal("—", PerfCounters.FormatDuration(null));
        Assert.Equal("0.4 ms", PerfCounters.FormatDuration(TimeSpan.FromMilliseconds(0.4)));
        Assert.Equal("380 ms", PerfCounters.FormatDuration(TimeSpan.FromMilliseconds(380)));
        Assert.Equal("1.24 s", PerfCounters.FormatDuration(TimeSpan.FromMilliseconds(1240)));
        Assert.Equal("1m 05s", PerfCounters.FormatDuration(TimeSpan.FromSeconds(65)));

        Assert.Equal("512 B", PerfCounters.FormatBytes(512));
        Assert.Equal("300 MB", PerfCounters.FormatBytes(300L * 1024 * 1024));
        Assert.Equal("1.5 GB", PerfCounters.FormatBytes(1610612736));
    }

    [Fact]
    public void Diagnostics_PerformanceViewModel_MapsCounters()
    {
        var clock = new ManualTimeProvider();
        var timeline = new StartupTimeline(clock);

        timeline.Mark(StartupTimeline.ProcessStart);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        timeline.Mark(StartupTimeline.WindowActivated);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        timeline.Mark(StartupTimeline.FirstSnapshot);
        clock.Advance(TimeSpan.FromMilliseconds(25));
        timeline.Mark(StartupTimeline.FirstProjectCardsReady);

        var counters = new PerfCounters();
        counters.RecordScan(TimeSpan.FromMilliseconds(380), 8, PerfOutcome.Ok);
        counters.RecordUpdateCheck(TimeSpan.FromMilliseconds(90), PerfOutcome.Ok);
        counters.RecordSettingsWrite(TimeSpan.FromMilliseconds(4), PerfOutcome.Ok);

        var viewModel = new DiagnosticsViewModel(new LogRingBufferSink(), counters, timeline);

        var windowActivated = Row(viewModel, "Startup · window activated");
        Assert.Equal("250 ms", windowActivated.ValueText);
        Assert.Contains("desktop UI automation", windowActivated.BudgetText, StringComparison.Ordinal);
        Assert.Equal(PerfTone.Muted, windowActivated.Tone);

        Assert.Equal("400 ms", Row(viewModel, "Startup · first snapshot").ValueText);
        var cardsReady = Row(viewModel, "Startup · project cards ready");
        Assert.Equal("425 ms", cardsReady.ValueText);
        Assert.Equal("budget 700 ms · M1", cardsReady.BudgetText);
        Assert.Equal(PerfTone.Within, cardsReady.Tone);

        var scan = Row(viewModel, "Last scan");
        Assert.Equal("380 ms · 8 repos", scan.ValueText);
        Assert.Equal(PerfTone.Within, scan.Tone);

        var updateCheck = Row(viewModel, "Last update check");
        Assert.Equal("90 ms", updateCheck.ValueText);
        Assert.Equal(PerfTone.Muted, updateCheck.Tone); // no local budget to compare against

        Assert.Equal("4 ms", Row(viewModel, "Last settings write").ValueText);
        Assert.Equal("—", Row(viewModel, "Max UI dispatch delay").ValueText);

        var memory = Row(viewModel, "Working set");
        Assert.NotEqual("—", memory.ValueText);
        Assert.NotEqual(PerfTone.Muted, memory.Tone);

        Assert.Contains("scans 1", viewModel.PerformanceCountersText, StringComparison.Ordinal);
        Assert.Contains("update checks 1", viewModel.PerformanceCountersText, StringComparison.Ordinal);
        Assert.Contains("settings writes 1", viewModel.PerformanceCountersText, StringComparison.Ordinal);
        Assert.Contains("failures 0", viewModel.PerformanceCountersText, StringComparison.Ordinal);
        Assert.Contains("ring evictions 0", viewModel.PerformanceCountersText, StringComparison.Ordinal);

        // Over budget is a colour, not a failure: the scan still counted as one.
        counters.RecordScan(TimeSpan.FromSeconds(2), 8, PerfOutcome.Ok);
        counters.RecordUiDispatchDelay(TimeSpan.FromMilliseconds(65));
        viewModel.Load();

        var slow = Row(viewModel, "Last scan");
        Assert.Equal("2.00 s · 8 repos", slow.ValueText);
        Assert.Equal(PerfTone.Over, slow.Tone);
        Assert.Contains("scans 2", viewModel.PerformanceCountersText, StringComparison.Ordinal);
        var uiDelay = Row(viewModel, "Max UI dispatch delay");
        Assert.Equal("65 ms", uiDelay.ValueText);
        Assert.Equal(PerfTone.Over, uiDelay.Tone);
    }

    [Fact]
    public void Diagnostics_PerformanceViewModel_NoSnapshotBeforeTheFirstScan()
    {
        var clock = new ManualTimeProvider();
        var timeline = new StartupTimeline(clock);

        timeline.Mark(StartupTimeline.ProcessStart);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        timeline.Mark(StartupTimeline.WindowActivated);

        // A launch that restores Diagnostics (or any page that asks for no cards)
        // never scans, so there is no "warm start to interactive cards" to report.
        // Total still answers "how far did startup get"; the snapshot row must not
        // borrow that number and call it M1.
        var viewModel = new DiagnosticsViewModel(new LogRingBufferSink(), new PerfCounters(), timeline);

        Assert.Equal(TimeSpan.FromMilliseconds(300), timeline.Total);

        var snapshot = Row(viewModel, "Startup · first snapshot");
        Assert.Equal("—", snapshot.ValueText);
        Assert.Equal(PerfTone.Muted, snapshot.Tone);
        Assert.Equal("300 ms", Row(viewModel, "Startup · window activated").ValueText);
        Assert.Equal("—", Row(viewModel, "Startup · project cards ready").ValueText);
        Assert.Equal("—", Row(viewModel, "Last scan").ValueText);
        Assert.Contains("scans 0", viewModel.PerformanceCountersText, StringComparison.Ordinal);
    }

    private static PerfRow Row(DiagnosticsViewModel viewModel, string label)
    {
        var row = viewModel.PerformanceRows.FirstOrDefault(candidate => candidate.Label == label);
        Assert.NotNull(row);
        return row!;
    }

    [Fact]
    public void PerfCounters_UiOperation_AttributesTheLongestCompletedOperation()
    {
        var clock = new ManualTimeProvider();
        var counters = new PerfCounters(clock);

        using (counters.BeginUiOperation("quick ui update"))
        {
            clock.Advance(TimeSpan.FromMilliseconds(5));
        }

        using (counters.BeginUiOperation("dashboard card layout"))
        {
            clock.Advance(TimeSpan.FromMilliseconds(70));
        }

        var operation = counters.TakeLongestUiOperationSinceLastSample();

        Assert.NotNull(operation);
        Assert.Equal("dashboard card layout", operation!.Value.Name);
        Assert.Equal(TimeSpan.FromMilliseconds(70), operation.Value.Elapsed);

        // A delay observed on the next heartbeat is attributed to that work, not
        // to whatever scope happens to be open when the tick finally runs.
        counters.RecordUiDispatchDelay(
            TimeSpan.FromMilliseconds(65),
            operation.Value.Name,
            operation.Value.Elapsed);

        Assert.Equal(TimeSpan.FromMilliseconds(65), counters.MaxUiDispatchDelay);
        Assert.Equal("dashboard card layout", counters.MaxUiDispatchDelayDuring);
        Assert.Equal(TimeSpan.FromMilliseconds(70), counters.MaxUiDispatchDelayOperationElapsed);

        // Taking the record resets it, so the next delay cannot borrow this name.
        Assert.Null(counters.TakeLongestUiOperationSinceLastSample());
    }

    [Fact]
    public void PerfCounters_UiOperation_NestedPrefersInnermostScope()
    {
        var clock = new ManualTimeProvider();
        var counters = new PerfCounters(clock);

        using (counters.BeginUiOperation("dashboard page load"))
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            using (counters.BeginUiOperation("dashboard xaml initialize"))
            {
                clock.Advance(TimeSpan.FromMilliseconds(60));
            }
        }

        var operation = counters.TakeLongestUiOperationSinceLastSample();

        Assert.NotNull(operation);
        Assert.Equal("dashboard xaml initialize", operation!.Value.Name);
        Assert.Equal(TimeSpan.FromMilliseconds(60), operation.Value.Elapsed);
    }

    [Fact]
    public void PerfCounters_BeginUiOperation_NoOperationActiveReportsNull()
    {
        var counters = new PerfCounters();
        counters.RecordUiDispatchDelay(TimeSpan.FromMilliseconds(10));

        Assert.Equal(TimeSpan.FromMilliseconds(10), counters.MaxUiDispatchDelay);
        Assert.Null(counters.MaxUiDispatchDelayDuring);
        Assert.Null(counters.MaxUiDispatchDelayOperationElapsed);
    }

    [Fact]
    public void PerfCounters_BeginUiOperation_PoppedAfterDispose()
    {
        var counters = new PerfCounters();

        using (counters.BeginUiOperation("first"))
        {
            counters.RecordUiDispatchDelay(TimeSpan.FromMilliseconds(5));
        }

        // Operation ended; the live stack must not leak it into later samples.
        counters.RecordUiDispatchDelay(TimeSpan.FromMilliseconds(50));
        Assert.Null(counters.MaxUiDispatchDelayDuring);
        Assert.Equal(TimeSpan.FromMilliseconds(50), counters.MaxUiDispatchDelay);
    }

    [Fact]
    public void Diagnostics_PerformanceViewModel_NamesTheAttributedUiOperation()
    {
        var clock = new ManualTimeProvider();
        var counters = new PerfCounters(clock);
        var viewModel = new DiagnosticsViewModel(
            new LogRingBufferSink(),
            counters,
            new StartupTimeline(clock));

        using (counters.BeginUiOperation("dashboard xaml initialize"))
        {
            clock.Advance(TimeSpan.FromMilliseconds(70));
        }

        var operation = counters.TakeLongestUiOperationSinceLastSample();
        Assert.NotNull(operation);
        counters.RecordUiDispatchDelay(
            TimeSpan.FromMilliseconds(80),
            operation!.Value.Name,
            operation.Value.Elapsed);

        viewModel.Load();

        var row = Row(viewModel, "Max UI dispatch delay");
        Assert.Equal("80 ms · during dashboard xaml initialize (70 ms)", row.ValueText);
        Assert.Equal(PerfTone.Over, row.Tone);
    }

    /// <summary>A clock whose timestamp only moves when the test says so.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
