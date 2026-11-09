using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Path identity is the foundation of every scan decision: whether a folder is
/// inside a root, whether a checkout belongs to a root, whether two paths are the
/// same repository. These cases cover the shapes a developer machine really has —
/// UNC roots and paths past the legacy 260-character limit — as pure path math,
/// so they run without a network share or a long-path-enabled volume.
/// </summary>
public sealed class PathHygieneTests
{
    [Fact]
    public void IsUnder_UncPathsCompareBySegment()
    {
        Assert.True(PathHygiene.IsUnder(
            @"\\server\share\ecosystem\repo",
            @"\\server\share\ecosystem"));

        // Same prefix, different segment: "ecosystem-archive" is not inside
        // "ecosystem", exactly like "DevDesk2" is not inside "DevDesk".
        Assert.False(PathHygiene.IsUnder(
            @"\\server\share\ecosystem-archive\repo",
            @"\\server\share\ecosystem"));
    }

    [Fact]
    public void IsUnder_UncRootEqualsItself()
    {
        Assert.True(PathHygiene.IsUnder(
            @"\\server\share\ecosystem",
            @"\\server\share\ecosystem"));
    }

    [Fact]
    public void IsUnder_DifferentUncShareIsNotUnderTheOtherShare()
    {
        Assert.False(PathHygiene.IsUnder(
            @"\\server\other-share\ecosystem\repo",
            @"\\server\share\ecosystem"));
    }

    [Fact]
    public void Canonicalize_KeepsUncPrefixAndRemovesTrailingSeparator()
    {
        Assert.Equal(
            @"\\server\share\ecosystem",
            PathHygiene.Canonicalize(@"\\server\share\ecosystem\"));
    }

    [Fact]
    public void IsUnder_HandlesPathsLongerThanTheLegacyLimit()
    {
        var parent = @"C:\Code\projects\DevEcosystem";
        var deep = parent + @"\" + string.Join(
            @"\",
            Enumerable.Repeat("a-very-long-folder-name-for-path-length", 6)) + @"\repo";

        Assert.True(deep.Length > 260);
        Assert.True(PathHygiene.IsUnder(deep, parent));
    }

    [Fact]
    public void Canonicalize_KeepsALongPathIntact()
    {
        var deep = @"C:\" + string.Join(
            @"\",
            Enumerable.Repeat("a-very-long-folder-name-for-path-length", 6)) + @"\repo";

        var canonical = PathHygiene.Canonicalize(deep);

        Assert.Equal(deep, canonical);
        Assert.Equal(deep.Length, canonical.Length);
    }
}
