using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleaseVersionSuggesterTests
{
    [Theory]
    [InlineData(ReleaseBumpKind.Patch, "0.6.1")]
    [InlineData(ReleaseBumpKind.Minor, "0.7.0")]
    [InlineData(ReleaseBumpKind.Major, "1.0.0")]
    public void TrySuggest_UsesTheExplicitBumpKind(
        ReleaseBumpKind bumpKind,
        string expectedVersion)
    {
        var succeeded = ReleaseVersionSuggester.TrySuggest(
            "0.6.0",
            bumpKind,
            ReleaseChannel.Beta,
            out var suggestion,
            out var error);

        Assert.True(succeeded, error);
        Assert.NotNull(suggestion);
        Assert.Equal("0.6.0", suggestion.BaselineVersion);
        Assert.Equal(expectedVersion, suggestion.Version);
        Assert.Equal(bumpKind, suggestion.BumpKind);
        Assert.Equal(ReleaseChannel.Beta, suggestion.Channel);
        Assert.Equal($"v{expectedVersion}-beta", suggestion.Tag);
    }

    [Theory]
    [InlineData(ReleaseChannel.Stable, "v0.6.1")]
    [InlineData(ReleaseChannel.Beta, "v0.6.1-beta")]
    [InlineData(ReleaseChannel.Dev, "v0.6.1-dev")]
    public void TrySuggestNextPatch_DefaultsToPatchAndFormatsTheTag(
        ReleaseChannel channel,
        string expectedTag)
    {
        var succeeded = ReleaseVersionSuggester.TrySuggestNextPatch(
            "0.6.0",
            channel,
            out var suggestion,
            out var error);

        Assert.True(succeeded, error);
        Assert.NotNull(suggestion);
        Assert.Equal(ReleaseBumpKind.Patch, suggestion.BumpKind);
        Assert.Equal(expectedTag, suggestion.Tag);
    }

    [Fact]
    public void TrySuggestFromTags_UsesHighestVersionOnTheSelectedChannel()
    {
        var tags = new[]
        {
            "v0.9.9-beta",
            "v0.10.0-beta",
            "v9.0.0-dev",
            "v99.0.0-alpha",
            "not-a-version"
        };

        var succeeded = ReleaseVersionSuggester.TrySuggestFromTags(
            tags,
            "0.1.0",
            ReleaseBumpKind.Patch,
            ReleaseChannel.Beta,
            out var suggestion,
            out var error);

        Assert.True(succeeded, error);
        Assert.NotNull(suggestion);
        Assert.Equal("0.10.0", suggestion.BaselineVersion);
        Assert.Equal("0.10.1", suggestion.Version);
        Assert.Equal("v0.10.1-beta", suggestion.Tag);
    }

    [Fact]
    public void TrySuggestFromTags_FallsBackToCurrentVersionWhenChannelHasNoTag()
    {
        var succeeded = ReleaseVersionSuggester.TrySuggestFromTags(
            ["v2.0.0", "v4.0.0-beta"],
            "0.7.2",
            ReleaseBumpKind.Patch,
            ReleaseChannel.Dev,
            out var suggestion,
            out var error);

        Assert.True(succeeded, error);
        Assert.NotNull(suggestion);
        Assert.Equal("0.7.2", suggestion.BaselineVersion);
        Assert.Equal("0.7.3", suggestion.Version);
        Assert.Equal("v0.7.3-dev", suggestion.Tag);
    }

    [Theory]
    [InlineData("v00.9.9-beta")]
    [InlineData("v0.09.9-beta")]
    [InlineData("v0.9.09-beta")]
    [InlineData("v1.0.0-stable")]
    [InlineData("v1.0.0-beta.1")]
    public void TrySuggestFromTags_IgnoresNonCanonicalTags(string invalidTag)
    {
        var succeeded = ReleaseVersionSuggester.TrySuggestFromTags(
            [invalidTag],
            "0.6.0",
            ReleaseBumpKind.Patch,
            ReleaseChannel.Beta,
            out var suggestion,
            out var error);

        Assert.True(succeeded, error);
        Assert.NotNull(suggestion);
        Assert.Equal("0.6.0", suggestion.BaselineVersion);
        Assert.Equal("0.6.1", suggestion.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 0.6.0")]
    [InlineData("0.6")]
    [InlineData("0.6.0.1")]
    [InlineData("00.6.0")]
    [InlineData("0.06.0")]
    [InlineData("0.6.00")]
    [InlineData("0.-1.0")]
    [InlineData("0.6.0-beta")]
    [InlineData("2147483648.0.0")]
    public void TrySuggest_RejectsInvalidNumericBaselines(string? baseline)
    {
        var succeeded = ReleaseVersionSuggester.TrySuggestNextPatch(
            baseline,
            ReleaseChannel.Beta,
            out var suggestion,
            out var error);

        Assert.False(succeeded);
        Assert.Null(suggestion);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("0.6.2147483647", ReleaseBumpKind.Patch)]
    [InlineData("0.2147483647.0", ReleaseBumpKind.Minor)]
    [InlineData("2147483647.0.0", ReleaseBumpKind.Major)]
    public void TrySuggest_ReportsComponentOverflow(string baseline, ReleaseBumpKind bumpKind)
    {
        var succeeded = ReleaseVersionSuggester.TrySuggest(
            baseline,
            bumpKind,
            ReleaseChannel.Beta,
            out var suggestion,
            out var error);

        Assert.False(succeeded);
        Assert.Null(suggestion);
        Assert.Contains("cannot be incremented", error);
    }
}
