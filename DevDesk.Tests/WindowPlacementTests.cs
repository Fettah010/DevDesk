using System.Collections.Generic;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Placement is the difference between "it remembered my window" and "DevDesk
/// started and showed nothing", so every rule is pinned with exact numbers rather
/// than "somewhere on screen": a window that is off by a title bar looks like a
/// crash to the person waiting for it.
/// </summary>
public class WindowPlacementTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1920, 1080);
    private static readonly ScreenRect Secondary = new(1920, 0, 1920, 1080);

    private static WindowSettings Saved(int x, int y, int width = 1240, int height = 800, bool maximized = false) =>
        new() { X = x, Y = y, Width = width, Height = height, Maximized = maximized };

    private static IReadOnlyList<ScreenRect> Displays(params ScreenRect[] displays) => displays;

    [Fact]
    public void Placement_ClampsToVisibleArea()
    {
        // Saved while a bigger monitor was attached: the window would be 1700 px
        // from the left on a 1920 px display, i.e. mostly past the right edge.
        var result = WindowPlacement.Restore(Saved(1700, 900), Displays(Primary));

        Assert.Equal(new ScreenRect(680, 280, 1240, 800), result.Bounds);
        Assert.False(result.Maximized);
    }

    [Fact]
    public void Placement_OffScreenCentres()
    {
        // The monitor this rectangle lived on is gone.
        var result = WindowPlacement.Restore(Saved(-4000, -4000), Displays(Primary));

        Assert.Equal(new ScreenRect(340, 140, 1240, 800), result.Bounds);
    }

    [Fact]
    public void Placement_CentresWhenTheSavedDisplayIsGone()
    {
        // Saved on the secondary display (x = 2000); only the primary is left.
        var result = WindowPlacement.Restore(Saved(2000, 100), Displays(Primary));

        Assert.Equal(new ScreenRect(340, 140, 1240, 800), result.Bounds);
    }

    [Fact]
    public void Placement_RoundTripsMaximised()
    {
        var result = WindowPlacement.Restore(Saved(100, 120, maximized: true), Displays(Primary));

        // The normal rectangle is what gets restored; the window is maximised after.
        Assert.Equal(new ScreenRect(100, 120, 1240, 800), result.Bounds);
        Assert.True(result.Maximized);
    }

    [Fact]
    public void Placement_EnforcesTheMinimumSize()
    {
        var result = WindowPlacement.Restore(Saved(10, 10, 200, 100), Displays(Primary));

        Assert.Equal(WindowPlacement.MinimumWidth, result.Bounds.Width);
        Assert.Equal(WindowPlacement.MinimumHeight, result.Bounds.Height);
        Assert.Equal(10, result.Bounds.X);
        Assert.Equal(10, result.Bounds.Y);
    }

    [Fact]
    public void Placement_ShrinksAWindowTooLargeForTheDisplay()
    {
        var result = WindowPlacement.Restore(Saved(0, 0, 3000, 2000), Displays(Primary));

        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), result.Bounds);
    }

    [Fact]
    public void Placement_UsesTheDisplayTheWindowWasOn()
    {
        // A window on the second monitor must stay there, not be dragged to the
        // primary one just because the primary is first in the list.
        var result = WindowPlacement.Restore(Saved(2000, 100), Displays(Primary, Secondary));

        Assert.Equal(new ScreenRect(2000, 100, 1240, 800), result.Bounds);
    }

    [Fact]
    public void Placement_RescuesATitleBarAboveTheScreen()
    {
        // Mostly visible, but the title bar (and therefore every caption button)
        // is above the top edge; sliding it down is the fix.
        var result = WindowPlacement.Restore(Saved(100, -700), Displays(Primary));

        Assert.Equal(new ScreenRect(100, 0, 1240, 800), result.Bounds);
    }

    [Fact]
    public void Placement_SurvivesNoDisplays()
    {
        // An unreadable display layout must not stop the window from opening.
        var result = WindowPlacement.Restore(Saved(500, 500, 1000, 700), Displays());

        Assert.Equal(new ScreenRect(500, 500, 1000, 700), result.Bounds);
    }
}
