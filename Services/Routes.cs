using System;
using System.Collections.Generic;

namespace DevDesk.Services;

/// <summary>
/// The one list of navigation destinations.
/// <para>
/// The NavigationView handler, the "restore last route" step and the settings file
/// all name destinations by these tags, so a tag cannot exist in one place and not
/// in another. An unknown tag in a hand-edited (or older, or newer) settings file
/// falls back to <see cref="Dashboard"/> instead of throwing or navigating nowhere.
/// </para>
/// </summary>
public static class Routes
{
    public const string Dashboard = "dashboard";
    public const string Projects = "projects";
    public const string Templates = "templates";
    public const string GitHub = "github";
    public const string Self = "self";
    public const string Settings = "settings";
    public const string Diagnostics = "diagnostics";

    /// <summary>Everything DevDesk has run: the queue now, plus the runs history kept.</summary>
    public const string Activity = "activity";

    /// <summary>Every destination, in the order the navigation view shows them.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Dashboard, Projects, Templates, GitHub, Self, Settings, Diagnostics, Activity,
    };

    public static string Default => Dashboard;

    public static bool IsKnown(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        foreach (var route in All)
        {
            if (string.Equals(route, tag.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The canonical tag for <paramref name="tag"/>, or <see cref="Dashboard"/>.</summary>
    public static string Normalize(string? tag) => IsKnown(tag) ? tag!.Trim().ToLowerInvariant() : Default;
}
