using System;

namespace DevDesk.Services;

/// <summary>Measures UI dispatcher lateness against a periodic heartbeat cadence.</summary>
public sealed class UiThreadHeartbeat
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private readonly TimeProvider _time;
    private readonly PerfCounters _counters;
    private long _previousTimestamp;
    private bool _hasPreviousSample;

    public UiThreadHeartbeat(PerfCounters? counters = null, TimeProvider? time = null)
    {
        _counters = counters ?? PerfCounters.Current;
        _time = time ?? TimeProvider.System;
    }

    public void Sample()
    {
        var timestamp = _time.GetTimestamp();
        if (!_hasPreviousSample)
        {
            _previousTimestamp = timestamp;
            _hasPreviousSample = true;
            return;
        }

        var elapsed = _time.GetElapsedTime(_previousTimestamp, timestamp);
        _previousTimestamp = timestamp;
        var delay = elapsed - Interval;
        var longestOperation = _counters.TakeLongestUiOperationSinceLastSample();
        if (delay > TimeSpan.Zero)
        {
            _counters.RecordUiDispatchDelay(
                delay,
                longestOperation?.Name,
                longestOperation?.Elapsed);
        }
    }
}
