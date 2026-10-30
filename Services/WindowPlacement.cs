using System;
using System.Collections.Generic;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>A rectangle on the virtual desktop, in physical pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public int OverlapWidth(ScreenRect other) => Math.Min(Right, other.Right) - Math.Max(X, other.X);

    public int OverlapHeight(ScreenRect other) => Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
}

/// <summary>
/// Where the window should open: the rectangle for its restored state, and
/// whether to maximise it afterwards.
/// </summary>
public sealed record WindowPlacementResult(ScreenRect Bounds, bool Maximized);

/// <summary>
/// Turns the geometry remembered in settings into a window that is actually on a
/// screen (roadmap F14).
/// <para>
/// Saved coordinates are a hint, not a command: the monitor they were saved on may
/// be gone, sitting to the left of the primary display, or have a different
/// resolution. A window that opens entirely off-screen looks exactly like a crash,
/// so the rule here is "always reachable" — the last saved rectangle is clamped
/// into the display it most belongs to, and centred on the primary display when no
/// display is even partly under it.
/// </para>
/// <para>
/// Pure geometry, no WinUI types: the caller passes the display rectangles it read
/// from <c>DisplayArea</c>, which is what makes this testable.
/// </para>
/// </summary>
public static class WindowPlacement
{
    public const int DefaultWidth = 1240;
    public const int DefaultHeight = 800;

    /// <summary>The smallest window the layout survives without looking broken.</summary>
    public const int MinimumWidth = 960;
    public const int MinimumHeight = 600;

    /// <summary>
    /// How much of the window must land inside a display for it to count as
    /// reachable there. Comfortably more than a title bar, so there is always
    /// something to grab with the mouse.
    /// </summary>
    public const int MinimumVisible = 64;

    /// <summary>
    /// Resolves <paramref name="saved"/> against the current displays, where
    /// <paramref name="displays"/>[0] is the primary display.
    /// </summary>
    public static WindowPlacementResult Restore(WindowSettings saved, IReadOnlyList<ScreenRect> displays)
    {
        var maximized = saved?.Maximized ?? false;

        if (displays is null || displays.Count == 0)
        {
            // No display information: trust the file and let the shell place the
            // window. Guessing a position would be worse than the saved one.
            return new WindowPlacementResult(
                new ScreenRect(
                    saved?.X ?? 0,
                    saved?.Y ?? 0,
                    saved?.Width ?? DefaultWidth,
                    saved?.Height ?? DefaultHeight),
                maximized);
        }

        var width = ClampSize(saved?.Width ?? DefaultWidth, MinimumWidth, Largest(displays, d => d.Width));
        var height = ClampSize(saved?.Height ?? DefaultHeight, MinimumHeight, Largest(displays, d => d.Height));

        var candidate = new ScreenRect(saved?.X ?? 0, saved?.Y ?? 0, width, height);
        var target = MostOverlapping(candidate, displays);

        if (target is null)
        {
            return new WindowPlacementResult(Center(displays[0], width, height), maximized);
        }

        // Slide the window fully into the display it belongs to. That is the same
        // operation whether it was one pixel past the right edge or sitting with
        // its title bar above the top edge.
        var bounds = new ScreenRect(
            Math.Clamp(candidate.X, target.Value.X, Math.Max(target.Value.X, target.Value.Right - width)),
            Math.Clamp(candidate.Y, target.Value.Y, Math.Max(target.Value.Y, target.Value.Bottom - height)),
            width,
            height);

        return new WindowPlacementResult(bounds, maximized);
    }

    /// <summary>The display with the largest overlap, if any is overlap enough.</summary>
    private static ScreenRect? MostOverlapping(ScreenRect window, IReadOnlyList<ScreenRect> displays)
    {
        ScreenRect? best = null;
        var bestArea = 0;

        foreach (var display in displays)
        {
            var overlapWidth = window.OverlapWidth(display);
            var overlapHeight = window.OverlapHeight(display);

            if (overlapWidth < MinimumVisible || overlapHeight < MinimumVisible)
            {
                continue;
            }

            var area = overlapWidth * overlapHeight;
            if (area > bestArea)
            {
                bestArea = area;
                best = display;
            }
        }

        return best;
    }

    private static ScreenRect Center(ScreenRect display, int width, int height) => new(
        display.X + Math.Max(0, (display.Width - width) / 2),
        display.Y + Math.Max(0, (display.Height - height) / 2),
        width,
        height);

    private static int Largest(IReadOnlyList<ScreenRect> displays, Func<ScreenRect, int> read)
    {
        var largest = 0;
        foreach (var display in displays)
        {
            largest = Math.Max(largest, read(display));
        }

        return largest;
    }

    /// <summary>Keeps a size usable on a small display without going below the minimum.</summary>
    private static int ClampSize(int value, int minimum, int available) =>
        Math.Clamp(value, minimum, Math.Max(minimum, available));
}
