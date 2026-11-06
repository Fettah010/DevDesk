using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDesk.Models;
using DevDesk.Services;
using Microsoft.UI.Xaml;
using Serilog.Events;

namespace DevDesk.ViewModels;

/// <summary>One entry of the level filter: a label and the minimum level it keeps.</summary>
public sealed record LevelOption(string Label, LogEventLevel? Minimum)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Diagnostics page's state: what is in the ring, what the filters keep, and
/// what the last export produced.
/// <para>
/// The view model is synchronous on purpose. Live appends arrive from the view,
/// which owns the dispatcher; a view model that marshalled threads itself would
/// be untestable and would hide where the UI thread actually is.
/// </para>
/// </summary>
public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly LogRingBufferSink _ring;
    private readonly PerfCounters _counters;
    private readonly StartupTimeline _timeline;
    private readonly List<LogEntry> _all = new();

    [ObservableProperty]
    public partial ObservableCollection<LogEntry> Entries { get; set; } = new();

    [ObservableProperty]
    public partial LevelOption SelectedLevel { get; set; }

    [ObservableProperty]
    public partial string TextFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AutoFollow { get; set; } = true;

    [ObservableProperty]
    public partial string CountText { get; set; } = "No log events yet";

    [ObservableProperty]
    public partial string BufferText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<PerfRow> PerformanceRows { get; set; } = Array.Empty<PerfRow>();

    [ObservableProperty]
    public partial string PerformanceCountersText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = "Export ready";

    [ObservableProperty]
    public partial string ExportPath { get; set; } = string.Empty;

    public DiagnosticsViewModel(
        LogRingBufferSink ring,
        PerfCounters? counters = null,
        StartupTimeline? timeline = null)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _counters = counters ?? PerfCounters.Current;
        _timeline = timeline ?? StartupTimeline.Current;

        Levels = new ReadOnlyCollection<LevelOption>(new List<LevelOption>
        {
            new("All levels", null),
            new("Warnings and errors", LogEventLevel.Warning),
            new("Errors only", LogEventLevel.Error),
            new("Info and above", LogEventLevel.Information),
            new("Debug and above", LogEventLevel.Debug),
        });

        SelectedLevel = Levels[0];
        Load();
    }

    public IReadOnlyList<LevelOption> Levels { get; }

    public bool IsEmpty => Entries.Count == 0;

    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;

    public bool IsStatusOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Snapshot of the ring, then filter it.</summary>
    public void Load()
    {
        _all.Clear();
        _all.AddRange(_ring.Snapshot());
        ApplyFilter();
        RefreshPerformance();
    }

    /// <summary>One live event, already redacted by the sink.</summary>
    public void Append(LogEntry entry)
    {
        _all.Add(entry);

        if (_all.Count > LogRingBufferSink.Capacity)
        {
            // The ring has already evicted, so a rebuild keeps the two in step.
            _all.RemoveRange(0, _all.Count - LogRingBufferSink.Capacity);
            ApplyFilter();
            return;
        }

        if (!Matches(entry))
        {
            UpdateCounts();
            return;
        }

        Entries.Add(entry);

        if (Entries.Count > LogRingBufferSink.Capacity)
        {
            Entries.RemoveAt(0);
        }

        UpdateCounts();
    }

    public void ClearView()
    {
        _all.Clear();
        Entries.Clear();
        UpdateCounts();
    }

    /// <summary>
    /// Writes the bundle. The destination comes from the view (a file picker is a
    /// UI concern); everything else — settings snapshot, environment, manifest —
    /// is the service's job.
    /// </summary>
    [RelayCommand]
    private Task ExportAsync(string? destinationPath) => RunBusyAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }

        var report = await EnvironmentReport.CollectAsync(channel: SettingsService.Current.Channel)
            .ConfigureAwait(true);

        var result = await DiagnosticsExport
            .CreateAsync(destinationPath, LoggingService.LogDirectoryPath, report)
            .ConfigureAwait(true);

        ExportPath = result.Path;
        Report("Export ready", result.Summary);
    });

    [RelayCommand]
    private Task ReloadAsync()
    {
        Load();
        StatusMessage = null;
        return Task.CompletedTask;
    }

    /// <summary>The lines currently visible, for copy-to-clipboard.</summary>
    public string VisibleText()
    {
        var builder = new StringBuilder();
        foreach (var entry in Entries)
        {
            builder.AppendLine(entry.Line);
        }

        return builder.ToString();
    }

    public bool Matches(LogEntry entry)
    {
        if (SelectedLevel?.Minimum is { } minimum && entry.Level < minimum)
        {
            return false;
        }

        var filter = TextFilter?.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        return entry.Message.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || entry.LevelText.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (entry.Exception?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void ApplyFilter()
    {
        var visible = _all.Where(Matches).TakeLast(LogRingBufferSink.Capacity).ToList();

        // Replacing the collection instance re-templates the list once instead of
        // firing 2000 collection-changed notifications.
        Entries = new ObservableCollection<LogEntry>(visible);
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        CountText = _all.Count == 0
            ? "No log events yet"
            : Entries.Count == _all.Count
                ? $"{_all.Count} event(s)"
                : $"{Entries.Count} of {_all.Count} event(s) shown";

        BufferText =
            $"Ring buffer {_ring.Count}/{LogRingBufferSink.Capacity} · {_ring.Dropped} evicted · " +
            $"{_ring.Failed} sink failure(s)";

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(ListVisibility));
    }

    /// <summary>
    /// Rebuilds the Performance card from the counters and the startup timeline.
    /// Read on demand (page load / Refresh) rather than on a timer: the numbers are
    /// a baseline, and a live-updating card would be a second scan of the same data
    /// on the UI thread.
    /// </summary>
    private void RefreshPerformance()
    {
        var repos = _counters.LastScanProjects is { } count ? $"{count} repos" : null;

        PerformanceRows = new List<PerfRow>
        {
            TimeRow(
                "Startup · window activated",
                _timeline.Elapsed(StartupTimeline.WindowActivated),
                null,
                "M2 appearance timing: desktop UI automation"),
            TimeRow(
                "Startup · first snapshot",
                _timeline.Elapsed(StartupTimeline.FirstSnapshot),
                null,
                "cache/catalog data ready; not a visual metric"),
            TimeRow(
                "Startup · project cards ready",
                _timeline.Elapsed(StartupTimeline.FirstProjectCardsReady),
                PerfBudgets.StartupTotal,
                "budget 700 ms · M1"),
            TimeRow(
                "Last scan",
                _counters.LastScan,
                PerfBudgets.Scan,
                "budget 1.5 s · M3",
                repos),
            TimeRow(
                "Last update check",
                _counters.LastUpdateCheck,
                null,
                "network — no local budget"),
            TimeRow(
                "Last settings write",
                _counters.LastSettingsWrite,
                PerfBudgets.SettingsWrite,
                "budget 20 ms · §6.3"),
            TimeRow(
                "Max UI dispatch delay",
                _counters.MaxUiDispatchDelay,
                PerfBudgets.UiDispatchDelay,
                "budget 50 ms · M6"),
            ByteRow(
                "Working set",
                _counters.WorkingSetBytes,
                PerfBudgets.WorkingSetBytes,
                "budget 300 MB · M5"),
        };

        // Ring evictions come from the ring, not from a second counter: one number,
        // one owner (see PerfCounters).
        PerformanceCountersText =
            $"scans {_counters.Scans} · update checks {_counters.UpdateChecks} · " +
            $"settings writes {_counters.SettingsWrites} · failures {_counters.Failures} · " +
            $"ring evictions {_ring.Dropped}";
    }

    private static PerfRow TimeRow(
        string label,
        TimeSpan? value,
        TimeSpan? budget,
        string budgetText,
        string? valueSuffix = null)
    {
        var text = PerfCounters.FormatDuration(value);
        if (valueSuffix is not null && value is not null)
        {
            text = $"{text} · {valueSuffix}";
        }

        return new PerfRow(label, text, budgetText, Compare(value?.TotalMilliseconds, budget?.TotalMilliseconds));
    }

    private static PerfRow ByteRow(string label, long value, long budget, string budgetText) =>
        new(
            label,
            PerfCounters.FormatBytes(value),
            budgetText,
            value <= budget ? PerfTone.Within : PerfTone.Over);

    private static PerfTone Compare(double? value, double? budget)
    {
        if (value is not { } measured || budget is not { } limit)
        {
            return PerfTone.Muted;
        }

        return measured <= limit ? PerfTone.Within : PerfTone.Over;
    }

    partial void OnSelectedLevelChanged(LevelOption value) => ApplyFilter();

    partial void OnTextFilterChanged(string value) => ApplyFilter();

    /// <summary>
    /// Shows a status message. Cleared first, so reporting the same thing twice
    /// re-opens the bar instead of looking like nothing happened.
    /// </summary>
    public void Report(string title, string message)
    {
        StatusTitle = title;
        StatusMessage = null;
        StatusMessage = message;
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(IsStatusOpen));
}
