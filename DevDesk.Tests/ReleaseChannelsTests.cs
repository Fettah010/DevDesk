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
    [InlineData(null, ReleaseChannel.Stable)]
    [InlineData("", ReleaseChannel.Stable)]
    [InlineData("stable", ReleaseChannel.Stable)]
    [InlineData("BETA", ReleaseChannel.Beta)]
    [InlineData(" dev ", ReleaseChannel.Dev)]
    public void TryParseSuffix_MapsKnownSuffixesToTypedChannels(string? suffix, ReleaseChannel expected)
    {
        Assert.True(ReleaseChannels.TryParseSuffix(suffix, out var channel));
        Assert.Equal(expected, channel);
        Assert.Equal(ReleaseChannels.FromSuffix(suffix), ReleaseChannels.ToName(channel));
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("nightly")]
    public void TryParseSuffix_RejectsUnknownChannels(string suffix)
    {
        Assert.False(ReleaseChannels.TryParseSuffix(suffix, out var channel));
        Assert.Equal(ReleaseChannel.Stable, channel);
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
    [InlineData("v0.1.0-beta", "0.1.0", ReleaseChannel.Beta)]
    [InlineData("0.1.0-dev", "0.1.0", ReleaseChannel.Dev)]
    [InlineData("v1.0.0", "1.0.0", ReleaseChannel.Stable)]
    public void TryParseTag_ProvidesTypedChannel(string tag, string version, ReleaseChannel channel)
    {
        Assert.True(ReleaseChannels.TryParseTagTyped(tag, out var parsedVersion, out var parsedChannel));
        Assert.Equal(version, parsedVersion);
        Assert.Equal(channel, parsedChannel);
        Assert.Equal(ReleaseChannels.ToName(parsedChannel), ReleaseChannels.FromSuffix(
            tag.Contains('-') ? tag[(tag.IndexOf('-') + 1)..] : null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v0.1")]
    [InlineData("v0.1.0-alpha")]
    [InlineData("nightly")]
    [InlineData("v0.1.0-beta.2")]
    [InlineData("v01.2.3-beta")]
    [InlineData("v1.2.3.4")]
    [InlineData("v2147483648.0.0")]
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
