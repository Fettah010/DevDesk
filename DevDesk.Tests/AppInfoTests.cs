using System;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class VersionInfoTests
{
    [Fact]
    public void Parse_BetaSuffix()
    {
        var info = VersionInfo.Parse("0.1.0-beta");

        Assert.Equal("0.1.0", info.Version);
        Assert.Equal("beta", info.Suffix);
        Assert.True(info.IsPrerelease);
        Assert.Equal("0.1.0-beta", info.Display);
    }

    [Fact]
    public void Parse_StripsBuildMetadata()
    {
        var info = VersionInfo.Parse("0.1.0-beta+2b1c9f4");

        Assert.Equal("0.1.0", info.Version);
        Assert.Equal("beta", info.Suffix);
        Assert.Equal("0.1.0-beta", info.Display);
    }

    [Fact]
    public void Parse_StableHasNoPrerelease()
    {
        var info = VersionInfo.Parse("1.0.0");

        Assert.Equal("1.0.0", info.Version);
        Assert.Equal(string.Empty, info.Suffix);
        Assert.False(info.IsPrerelease);
        Assert.Equal("1.0.0", info.Display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void Parse_NeverThrowsOnGarbage(string? raw)
    {
        var info = VersionInfo.Parse(raw);

        Assert.Equal(VersionInfo.Fallback, info.Version);
        Assert.False(info.IsPrerelease);
    }
}

public class AppInfoTests
{
    [Fact]
    public void AppInfo_AgreesWithCsprojVersion()
    {
        var declared = VersionSyncTests.ReadDeclaredVersion();

        Assert.Equal(declared.Version, AppInfo.Current.Version);
        Assert.Equal(declared.InformationalVersion, AppInfo.Current.DisplayVersion);
    }

    [Fact]
    public void AppInfo_ReportsPrereleaseFromSuffix()
    {
        var info = AppInfo.Current;

        Assert.Equal(info.Suffix.Length > 0, info.IsPrerelease);
        Assert.False(string.IsNullOrWhiteSpace(info.DisplayVersion));
    }

    [Fact]
    public void AppInfo_RepositoryUrl_IsDevDesk_NotTemplate()
    {
        var url = AppInfo.Current.RepositoryUrl;

        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.Contains("DevDesk", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("winui-3-easy-template", url, StringComparison.OrdinalIgnoreCase);
    }
}
