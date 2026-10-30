using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The last route is restored from a file a user can edit, from an older build, or
/// from a newer build that knows a page this one does not. None of those may leave
/// the app on a blank frame.
/// </summary>
public class RoutesTests
{
    [Theory]
    [InlineData("dashboard")]
    [InlineData("projects")]
    [InlineData("templates")]
    [InlineData("github")]
    [InlineData("self")]
    [InlineData("settings")]
    [InlineData("diagnostics")]
    public void Routes_EveryKnownTagSurvivesTheRoundTrip(string tag)
    {
        Assert.True(Routes.IsKnown(tag));
        Assert.Equal(tag, Routes.Normalize(tag));
        Assert.Contains(tag, Routes.All);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("activity")]
    [InlineData("Dashboard ")] // trimmed, and matching is case-insensitive
    public void Routes_UnknownTagFallsBackToDashboard(string? tag)
    {
        var route = Routes.Normalize(tag);

        Assert.Equal(Routes.Dashboard, route);
        Assert.Contains(route, Routes.All);
    }

    [Fact]
    public void Routes_CaseIsNormalisedToTheTagTheViewUses()
    {
        Assert.Equal(Routes.Settings, Routes.Normalize("SETTINGS"));
    }

    [Fact]
    public void Routes_AllHasNoDuplicates()
    {
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        foreach (var route in Routes.All)
        {
            Assert.True(seen.Add(route), $"{route} appears twice");
        }
    }
}
