using System;
using System.IO;
using DevDesk.Models;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The build keys are additive (schema stays 1), so the risks are the ones
/// additive keys always have: losing a value on the next save, losing a key a
/// newer build wrote, and two spellings of the same Windows path becoming two
/// profiles.
/// </summary>
public class BuildSettingsTests
{
    [Fact]
    public void Build_DefaultsRoundTripThroughTheFile()
    {
        using var sandbox = new SettingsSandbox();
        var settings = sandbox.NewService();

        settings.Save();

        var json = sandbox.ReadJson();
        Assert.Equal("Debug", json["build"]!["configuration"]!.GetValue<string>());
        Assert.Equal("x64", json["build"]!["platform"]!.GetValue<string>());
        Assert.Equal(200, json["build"]!["historyLimit"]!.GetValue<int>());
        Assert.True(json["build"]!["followTail"]!.GetValue<bool>());
        Assert.False(json["build"]!["noRestore"]!.GetValue<bool>());
    }

    [Fact]
    public void Build_AChangedValueSurvivesAReload()
    {
        using var sandbox = new SettingsSandbox();
        var first = sandbox.NewService();
        first.Settings.Build.Configuration = "Release";
        first.Settings.Build.NoRestore = true;
        first.Settings.Build.HistoryLimit = 50;
        first.Save();

        var second = sandbox.NewService();

        Assert.Equal("Release", second.Settings.Build.Configuration);
        Assert.True(second.Settings.Build.NoRestore);
        Assert.Equal(50, second.Settings.Build.HistoryLimit);
    }

    [Fact]
    public void BuildProfiles_ARoundTripWithOnlyTheSetValues()
    {
        using var sandbox = new SettingsSandbox();
        var path = Path.Combine(Path.GetTempPath(), "DevEcosystem", "Formixa");
        var first = sandbox.NewService();

        first.Settings.BuildProfiles[path] = new BuildProfileSettings
        {
            Configuration = "Release",
            ExtraArgs = "/p:RunAnalyzers=false",
        };
        first.Save();

        var second = sandbox.NewService();
        Assert.True(second.Settings.BuildProfiles.TryGetValue(path, out var profile));
        Assert.Equal("Release", profile!.Configuration);
        Assert.Equal("/p:RunAnalyzers=false", profile.ExtraArgs);

        // An unset value is absent, not an empty string that would override the default.
        Assert.Null(profile.Platform);
        Assert.Null(profile.NoRestore);
    }

    [Fact]
    public void BuildProfiles_KeysAreNormalisedAndCaseInsensitive()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile(
            "{\"schemaVersion\":1,\"buildProfiles\":{\"C:/Code/Projects/Formixa/\":{\"configuration\":\"Release\"}}}");

        var settings = sandbox.NewService();

        var key = Path.GetFullPath(@"C:\Code\Projects\Formixa");
        Assert.True(settings.Settings.BuildProfiles.ContainsKey(key), "the normalised key was not found");
        Assert.True(settings.Settings.BuildProfiles.ContainsKey(key.ToUpperInvariant()));
        Assert.Equal("Release", settings.Settings.BuildProfiles[key].Configuration);
    }

    [Fact]
    public void Build_UnknownKeysInsideTheBuildObjectSurviveASave()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile(
            "{\"schemaVersion\":1,\"build\":{\"configuration\":\"Debug\",\"futureKey\":42},\"unknownTop\":{\"a\":1}}");

        var settings = sandbox.NewService();
        settings.Save();

        var json = sandbox.ReadJson();
        Assert.Equal(42, json["build"]!["futureKey"]!.GetValue<int>());
        Assert.Equal(1, json["unknownTop"]!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Build_AnUnknownPlatformFallsBackToX64()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("{\"schemaVersion\":1,\"build\":{\"platform\":\"MIPS\"}}");

        var settings = sandbox.NewService();

        Assert.Equal("x64", settings.Settings.Build.Platform);
    }

    [Fact]
    public void Build_AHandEditedHistoryLimitIsClamped()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("{\"schemaVersion\":1,\"build\":{\"historyLimit\":-5}}");

        var settings = sandbox.NewService();

        Assert.Equal(1, settings.Settings.Build.HistoryLimit);
    }
}
