using System;

namespace DevDesk.Tests;

/// <summary>
/// A clock whose timestamp and UTC time only move when a test says so. Shared by
/// the build-surface tests so a duration is asserted instead of raced: the same
/// reason production code takes a <see cref="TimeProvider"/> rather than a
/// <c>Stopwatch</c> (AGENTS gotcha 15).
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
