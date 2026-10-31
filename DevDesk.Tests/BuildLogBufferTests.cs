using System;
using DevDesk.ViewModels;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The console buffer's whole job is to be bounded without lying: the newest
/// lines stay, the count of the ones it let go is available, and clearing starts
/// a new run at zero.
/// </summary>
public class BuildLogBufferTests
{
    private static LogLine Line(int index) =>
        new($"line {index}", IsError: false, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Add_KeepsTheNewestLinesAndCountsTheDroppedOnes()
    {
        var buffer = new BuildLogBuffer(capacity: 3);

        var evicted = 0;
        for (var index = 1; index <= 5; index++)
        {
            evicted += buffer.Add(Line(index));
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, buffer.DroppedCount);
        Assert.Equal(2, evicted);
        Assert.Equal(new[] { "line 3", "line 4", "line 5" },
            new[] { buffer.Items[0].Text, buffer.Items[1].Text, buffer.Items[2].Text });
    }

    [Fact]
    public void Add_ReturnsZeroUntilTheCapacityIsReached()
    {
        var buffer = new BuildLogBuffer(capacity: 3);

        Assert.Equal(0, buffer.Add(Line(1)));
        Assert.Equal(0, buffer.Add(Line(2)));
        Assert.Equal(0, buffer.Add(Line(3)));
        Assert.Equal(1, buffer.Add(Line(4)));
    }

    [Fact]
    public void Clear_EmptiesTheBufferAndTheDroppedCount()
    {
        var buffer = new BuildLogBuffer(capacity: 2);
        buffer.Add(Line(1));
        buffer.Add(Line(2));
        buffer.Add(Line(3));

        buffer.Clear();

        Assert.True(buffer.IsEmpty);
        Assert.Equal(0, buffer.DroppedCount);
        Assert.Empty(buffer.Items);
    }

    [Fact]
    public void Capacity_IsAtLeastOne()
    {
        var buffer = new BuildLogBuffer(capacity: 0);

        buffer.Add(Line(1));
        buffer.Add(Line(2));

        Assert.Equal(1, buffer.Capacity);
        Assert.Equal(1, buffer.Count);
        Assert.Equal("line 2", buffer.Items[0].Text);
    }

    [Fact]
    public void DefaultCapacity_IsTwentyThousandLines()
    {
        Assert.Equal(20_000, new BuildLogBuffer().Capacity);
    }
}
