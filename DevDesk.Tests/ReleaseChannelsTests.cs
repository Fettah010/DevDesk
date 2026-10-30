using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The channel vocabulary is the contract between the tag, the release script,
/// the updater and the UI (roadmap D4/O2). These tests pin it down.
/// </summary>
public class ReleaseChannelsTests
{
    [Fact]
    public void Vocabulary_IsExactlyStableBetaDev()
    {
        Assert.Equal(new[] { "stable", "beta", "dev" }, ReleaseChannels.All);
        Assert.Equal("beta", ReleaseChannels.Default);
    }

    [Theory]
    [InlineData(null, "stable")]
    [InlineData("", "stable")]
    [InlineData("stable", "stable")]
    [InlineData("beta", "beta")]
    [InlineData("BETA", "beta")]
    [InlineData(" dev ", "dev")]
    public void FromSuffix_MapsKnownSuffixes(string? suffix, string expected)
    {
        Assert.Equal(expected, ReleaseChannels.FromSuffix(suffix));
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("rc1")]
    [InlineData("release")]
    public void FromSuffix_RejectsRetiredAndUnknownSuffixes(string suffix)
    {
        Assert.Null(ReleaseChannels.FromSuffix(suffix));
    }

    [Theory]
    [InlineData("v0.1.0-beta", "0.1.0", "beta")]
    [InlineData("0.1.0-dev", "0.1.0", "dev")]
    [InlineData("v1.0.0", "1.0.0", "stable")]
    [InlineData("V2.3.4-BETA", "2.3.4", "beta")]
    public void TryParseTag_DerivesVersionAndChannel(string tag, string version, string channel)
    {
        Assert.True(ReleaseChannels.TryParseTag(tag, out var parsedVersion, out var parsedChannel));
        Assert.Equal(version, parsedVersion);
        Assert.Equal(channel, parsedChannel);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v0.1")]
    [InlineData("v0.1.0-alpha")]
    [InlineData("nightly")]
    [InlineData("v0.1.0-beta.2")]
    public void TryParseTag_RefusesAnythingItCannotMap(string? tag)
    {
        Assert.False(ReleaseChannels.TryParseTag(tag, out var version, out var channel));
        Assert.Equal(string.Empty, version);
        Assert.Equal(ReleaseChannels.Stable, channel);
    }

    [Fact]
    public void IsKnown_AcceptsOnlyTheVocabulary()
    {
        Assert.True(ReleaseChannels.IsKnown("stable"));
        Assert.True(ReleaseChannels.IsKnown("beta"));
        Assert.True(ReleaseChannels.IsKnown("dev"));
        Assert.False(ReleaseChannels.IsKnown("alpha"));
        Assert.False(ReleaseChannels.IsKnown(null));
    }
}
