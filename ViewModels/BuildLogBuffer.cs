using System;
using System.Collections.Generic;

namespace DevDesk.ViewModels;

/// <summary>One line the console shows. Immutable apart from nothing: the buffer owns the list.</summary>
public sealed record LogLine(string Text, bool IsError, DateTimeOffset At);

/// <summary>
/// The console's bounded window on a build's output (D-0.3.5).
/// <para>
/// It keeps the newest <see cref="Capacity"/> lines and counts the ones it let
/// go, because the UI must be able to say "12,431 earlier lines are not shown"
/// instead of silently pretending the output started here. The complete text is
/// always in the log file, so nothing is actually lost.
/// </para>
/// <para>
/// No UI types and no dispatcher: this is testable without a window, and the
/// console control is the only thing that mirrors it into an observable list.
/// </para>
/// </summary>
public sealed class BuildLogBuffer
{
    public const int DefaultCapacity = 20_000;

    private readonly Queue<LogLine> _lines = new();

    public BuildLogBuffer(int capacity = DefaultCapacity) =>
        Capacity = Math.Max(1, capacity);

    public int Capacity { get; }

    public int Count => _lines.Count;

    public bool IsEmpty => _lines.Count == 0;

    /// <summary>Lines evicted since the last <see cref="Clear"/>.</summary>
    public long DroppedCount { get; private set; }

    /// <summary>
    /// Appends one line and returns how many were evicted to make room. The
    /// caller removes that many rows from the front of the visible list, which is
    /// what keeps the view and the buffer the same size.
    /// </summary>
    public int Add(LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        _lines.Enqueue(line);

        var evicted = 0;
        while (_lines.Count > Capacity)
        {
            _lines.Dequeue();
            evicted++;
        }

        DroppedCount += evicted;
        return evicted;
    }

    public IReadOnlyList<LogLine> Items => _lines.ToArray();

    public void Clear()
    {
        _lines.Clear();
        DroppedCount = 0;
    }
}
