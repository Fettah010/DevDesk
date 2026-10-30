using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using DevDesk.Models;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace DevDesk.Services;

/// <summary>
/// A bounded in-memory log ring (roadmap §6.4): the last
/// <see cref="Capacity"/> events, so Diagnostics can show what happened during
/// this launch without reading a file and without growing forever.
/// <para>
/// Sinks run inside every <c>Log.*</c> call, so nothing here may throw: a
/// failing sink would take an unrelated operation down with it. Failures are
/// counted and exposed instead of rethrown.
/// </para>
/// <para>
/// The event is rendered and redacted once, at the point of capture
/// (<see cref="LogRedaction"/>), and only the rendered text is kept.
/// </para>
/// </summary>
public sealed class LogRingBufferSink : ILogEventSink
{
    public const int Capacity = 2000;

    // The file sink's own message token, so a line copied from Diagnostics is
    // identical to the same line in applog-*.log. Rendering with the default
    // RenderMessage() would quote every string and print False for false.
    private static readonly MessageTemplateTextFormatter MessageRenderer = new("{Message:lj}");

    private readonly object _gate = new();
    private readonly LogEntry?[] _items = new LogEntry?[Capacity];

    private int _start;
    private int _count;
    private long _written;
    private long _failed;
    private long _dropped;

    /// <summary>
    /// Raised after an entry is stored, on the logging thread — a subscriber must
    /// marshal to the UI thread itself. Raised outside the lock, so a slow handler
    /// cannot block other loggers.
    /// </summary>
    public event EventHandler<LogEntry>? Appended;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>How many events have ever been offered to the sink.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Events evicted to stay within <see cref="Capacity"/>.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Times a sink call failed. Must stay 0; surfaced so it cannot hide.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return;
        }

        LogEntry entry;
        try
        {
            entry = new LogEntry(
                logEvent.Timestamp,
                logEvent.Level,
                LogRedaction.Redact(RenderMessage(logEvent)),
                logEvent.Exception is null ? null : LogRedaction.Redact(logEvent.Exception.ToString()));
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failed);
            return;
        }

        Interlocked.Increment(ref _written);
        Push(entry);

        try
        {
            Appended?.Invoke(this, entry);
        }
        catch (Exception)
        {
            // A broken subscriber is not allowed to fail the logger.
            Interlocked.Increment(ref _failed);
        }
    }

    /// <summary>The buffered entries, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new LogEntry[_count];
            for (var index = 0; index < _count; index++)
            {
                result[index] = _items[(_start + index) % Capacity]!;
            }

            return result;
        }
    }

    /// <summary>Forgets everything currently buffered; the counters stay monotonic.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _start = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Renders the message exactly as the file sink's template does
    /// (<c>{Message:lj}</c>), then redacts it. Kept separate from the line's
    /// timestamp and level so <see cref="LogEntry.Line"/> can reproduce the file.
    /// </summary>
    private static string RenderMessage(LogEvent logEvent)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        MessageRenderer.Format(logEvent, buffer);
        return buffer.ToString();
    }

    private void Push(LogEntry entry)
    {
        lock (_gate)
        {
            if (_count < Capacity)
            {
                _items[(_start + _count) % Capacity] = entry;
                _count++;
                return;
            }

            _items[_start] = entry;
            _start = (_start + 1) % Capacity;
        }

        Interlocked.Increment(ref _dropped);
    }
}
