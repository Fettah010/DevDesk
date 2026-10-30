using System;
using System.Collections.Generic;

namespace DevDesk.Services;

/// <summary>One startup mark: where it happened, measured from the first line of Main.</summary>
public sealed record StartupMark(string Name, TimeSpan Elapsed);

/// <summary>
/// The startup marks from roadmap §6.3, measured from one origin so the number in
/// the Diagnostics card is the same number in the log.
/// <para>
/// Marks can be taken before logging exists — the first one is the first line of
/// Main, long before <see cref="LoggingService.Initialize"/> — so they are
/// buffered and flushed in order once the logger is ready
/// (<see cref="AttachToLog"/>). A name is recorded once: a second mark with the
/// same name (a re-navigation, a refresh) is ignored, so "window shown" really
/// means the first time it was shown.
/// </para>
/// <para>
/// The clock is a <see cref="TimeProvider"/> and the origin is its timestamp at
/// the first line of Main rather than <c>Environment.TickCount64</c>: every later
/// mark must be measured with the same clock, and the provider is what lets a test
/// inject a fixed clock instead of racing the wall. <c>TickCount64</c> is
/// boot-relative and millisecond-granular, which would round the fast marks to 0.
/// </para>
/// <para>
/// Marks are taken from the UI thread, the scan thread and the network thread, so
/// the list is guarded; logging happens outside the lock, because a sink is
/// allowed to be slow and startup is not allowed to wait for it.
/// </para>
/// </summary>
public sealed class StartupTimeline
{
    public const string ProcessStart = "process start";
    public const string SettingsLoaded = "settings loaded";
    public const string LoggingReady = "logging ready";
    public const string VelopackDone = "velopack done";
    public const string WindowShown = "window shown";
    public const string FirstSnapshot = "first snapshot";

    public static StartupTimeline Current { get; } = new();

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly List<StartupMark> _marks = new();

    private bool _logReady;
    private int _logged;

    public StartupTimeline(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
    }

    /// <summary>The marks taken so far, in the order they actually happened.</summary>
    public IReadOnlyList<StartupMark> Marks
    {
        get
        {
            lock (_gate)
            {
                return _marks.ToArray();
            }
        }
    }

    /// <summary>Where the last mark landed: the startup total, or null before the first one.</summary>
    public TimeSpan? Total
    {
        get
        {
            lock (_gate)
            {
                return _marks.Count == 0 ? null : _marks[^1].Elapsed;
            }
        }
    }

    /// <summary>
    /// Records a mark and returns its offset from process start. A repeated name
    /// returns the first recorded offset and changes nothing.
    /// </summary>
    public TimeSpan Mark(string name)
    {
        StartupMark mark;
        var logNow = false;

        lock (_gate)
        {
            foreach (var existing in _marks)
            {
                if (existing.Name == name)
                {
                    return existing.Elapsed;
                }
            }

            mark = new StartupMark(name, _time.GetElapsedTime(_origin, _time.GetTimestamp()));
            _marks.Add(mark);
            logNow = _logReady;
        }

        if (logNow)
        {
            Write(mark);
        }

        return mark.Elapsed;
    }

    /// <summary>Offset of a named mark, or null when it never happened this session.</summary>
    public TimeSpan? Elapsed(string name)
    {
        lock (_gate)
        {
            foreach (var mark in _marks)
            {
                if (mark.Name == name)
                {
                    return mark.Elapsed;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Called by <see cref="LoggingService.Initialize"/>: writes everything taken
    /// before the logger existed, in the order the marks really happened, then
    /// writes each new mark as it is taken. Each mark is logged exactly once.
    /// </summary>
    public void AttachToLog()
    {
        StartupMark[] pending;

        lock (_gate)
        {
            _logReady = true;
            pending = _marks.GetRange(_logged, _marks.Count - _logged).ToArray();
            _logged = _marks.Count;
        }

        foreach (var mark in pending)
        {
            Write(mark);
        }
    }

    private static void Write(StartupMark mark) =>
        LoggingService.Log.Information(
            "Startup: {Stage} at +{Elapsed} ms",
            mark.Name,
            (long)mark.Elapsed.TotalMilliseconds);
}
