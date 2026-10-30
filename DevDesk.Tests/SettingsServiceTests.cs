using System;
using System.IO;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Settings must survive a crash during save, a version bump, a hand edit and a
/// corrupt byte — and must never stop the app from starting.
/// </summary>
public class SettingsServiceTests
{
    [Fact]
    public void Settings_MigratesLegacyThemeOnly()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("{ \"theme\": \"Dark\" }");

        var service = sandbox.NewService();

        Assert.Equal(SettingsLoadStatus.Migrated, service.LastLoad.Status);
        Assert.Equal(AppTheme.Dark, service.Theme);

        service.Save();

        var json = sandbox.ReadJson();
        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("Dark", json["theme"]!.GetValue<string>());
        Assert.NotNull(json["roots"]);
        Assert.NotNull(json["window"]);
    }

    [Fact]
    public void Settings_PreservesUnknownKeys()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile(
            """
            {
              "schemaVersion": 1,
              "theme": "Dark",
              "futureFlag": { "nested": true },
              "window": { "x": 5, "extra": "keep" },
              "roots": [ { "path": "C:\\x", "depth": 2, "unknownRootKey": 7 } ]
            }
            """);

        var service = sandbox.NewService();
        service.Theme = AppTheme.Light;
        service.Save();

        var json = sandbox.ReadJson();
        Assert.True(json["futureFlag"]!["nested"]!.GetValue<bool>());
        Assert.Equal("keep", json["window"]!["extra"]!.GetValue<string>());
        Assert.Equal(5, json["window"]!["x"]!.GetValue<int>());
        Assert.Equal(7, json["roots"]![0]!["unknownRootKey"]!.GetValue<int>());
        Assert.Equal("Light", json["theme"]!.GetValue<string>());
    }

    [Fact]
    public void Settings_CorruptFileIsBackedUp()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("{ this is not json");

        var service = sandbox.NewService();

        Assert.Equal(SettingsLoadStatus.Corrupt, service.LastLoad.Status);
        Assert.True(service.LastLoad.HasWarning);
        Assert.NotNull(service.LastLoad.BackupPath);
        Assert.True(File.Exists(service.LastLoad.BackupPath!));
        Assert.False(File.Exists(sandbox.SettingsPath));

        // Defaults are usable, and the app can still save afterwards.
        Assert.Equal(AppTheme.System, service.Theme);
        service.Theme = AppTheme.Dark;
        Assert.True(File.Exists(sandbox.SettingsPath));
    }

    [Fact]
    public void Settings_LeftoverTempFileFromACrashedSaveIsIgnored()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("""{ "schemaVersion": 1, "theme": "Dark" }""");

        // What a crash between "write temp" and "swap" leaves behind.
        File.WriteAllText(sandbox.SettingsPath + ".tmp", "{ half a docu");

        var service = sandbox.NewService();

        Assert.Equal(SettingsLoadStatus.Loaded, service.LastLoad.Status);
        Assert.Equal(AppTheme.Dark, service.Theme);

        service.Save();

        Assert.False(File.Exists(sandbox.SettingsPath + ".tmp"));
        Assert.Equal("Dark", sandbox.ReadJson()["theme"]!.GetValue<string>());
    }

    [Fact]
    public void Settings_AtomicWriteLeavesNoTemp()
    {
        using var sandbox = new SettingsSandbox();
        var service = sandbox.NewService();

        service.Theme = AppTheme.Dark;
        service.Save();

        Assert.False(File.Exists(sandbox.SettingsPath + ".tmp"));
        Assert.NotNull(sandbox.ReadJson());
    }

    [Fact]
    public void Settings_InvalidChannelFallsBackToBeta()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("""{ "schemaVersion": 1, "channel": "alpha", "defaultReleaseChannel": "nightly" }""");

        var service = sandbox.NewService();

        Assert.Equal(ReleaseChannels.Beta, service.Channel);
        Assert.Equal(ReleaseChannels.Beta, service.Settings.DefaultReleaseChannel);

        service.Save();
        Assert.Equal("beta", sandbox.ReadJson()["channel"]!.GetValue<string>());
    }

    [Fact]
    public void Settings_RoundTripsWindowPlacement()
    {
        using var sandbox = new SettingsSandbox();
        var service = sandbox.NewService();

        service.Settings.Window = new WindowSettings
        {
            X = 11,
            Y = 22,
            Width = 1333,
            Height = 777,
            Maximized = true,
        };
        service.Save();

        var reloaded = sandbox.NewService();

        Assert.Equal(11, reloaded.Settings.Window.X);
        Assert.Equal(22, reloaded.Settings.Window.Y);
        Assert.Equal(1333, reloaded.Settings.Window.Width);
        Assert.Equal(777, reloaded.Settings.Window.Height);
        Assert.True(reloaded.Settings.Window.Maximized);
    }

    [Fact]
    public void Settings_RestoresTheLastRouteAndRefusesAnUnknownOne()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("""{ "schemaVersion": 1, "lastRoute": "projects" }""");

        var service = sandbox.NewService();
        Assert.Equal(Routes.Projects, service.LastRoute);

        // A route this build does not have (an old or a newer one) is not stored.
        service.LastRoute = "activity";
        Assert.Equal(Routes.Dashboard, service.LastRoute);
        Assert.Equal("dashboard", sandbox.ReadJson()["lastRoute"]!.GetValue<string>());

        service.LastRoute = Routes.Diagnostics;
        Assert.Equal("diagnostics", sandbox.ReadJson()["lastRoute"]!.GetValue<string>());
        Assert.Equal(Routes.Diagnostics, sandbox.NewService().LastRoute);
    }

    [Fact]
    public void Settings_DefaultsDescribeTheRealScanRoot()
    {
        using var sandbox = new SettingsSandbox();

        var service = sandbox.NewService();
        var root = service.Settings.Roots[0];

        Assert.Equal(SettingsLoadStatus.Missing, service.LastLoad.Status);
        Assert.False(File.Exists(sandbox.SettingsPath)); // reading must not create a file
        Assert.Equal(ScanEngine.DefaultScanRoot, root.Path);
        Assert.Equal(ScanEngine.DefaultMaxDepth, root.Depth);
        Assert.Equal(PathHygiene.IgnoredFolderNames, root.Ignores);
    }

    [Fact]
    public void Settings_UnknownThemeFallsBackToSystem()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("""{ "schemaVersion": 1, "theme": "Rainbow" }""");

        Assert.Equal(AppTheme.System, sandbox.NewService().Theme);
    }

    [Fact]
    public void Settings_HandEditedTypesFallBackToDefaults()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile(
            """{ "schemaVersion": 1, "theme": 42, "channel": true, "roots": [ { "path": 9, "depth": "deep" } ] }""");

        var service = sandbox.NewService();
        var root = service.Settings.Roots[0];

        Assert.Equal(AppTheme.System, service.Theme);
        Assert.Equal(ReleaseChannels.Beta, service.Channel);
        Assert.Equal(ScanEngine.DefaultScanRoot, root.Path);
        Assert.Equal(ScanEngine.DefaultMaxDepth, root.Depth);
    }

    [Fact]
    public void Settings_FileFromANewerBuild_KeepsItsSchemaAndKeys()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("""{ "schemaVersion": 7, "theme": "Dark", "futureThing": 1 }""");

        var service = sandbox.NewService();
        service.Save();

        var json = sandbox.ReadJson();
        Assert.Equal(7, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal(1, json["futureThing"]!.GetValue<int>());
        Assert.Equal("Dark", json["theme"]!.GetValue<string>());
    }

    [Fact]
    public void Settings_ChannelReachesTheUpdater()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteFile("""{ "schemaVersion": 1, "channel": "dev" }""");

        var service = sandbox.NewService();
        var original = UpdateService.Current.Channel;

        try
        {
            AppServices.Initialize(new ProjectCatalog(new FakeScanEngine()), service);

            Assert.Equal(ReleaseChannels.Dev, UpdateService.Current.Channel);
            Assert.Same(service, AppServices.Settings);
        }
        finally
        {
            UpdateService.Current.Channel = original;
        }
    }

    [Fact]
    public void Settings_DataDirectoryCanBeOverriddenByEnvironment()
    {
        var original = Environment.GetEnvironmentVariable(SettingsService.DataDirectoryVariable);
        var custom = Path.Combine(Path.GetTempPath(), "devdesk-data-" + Guid.NewGuid().ToString("N"));

        try
        {
            Environment.SetEnvironmentVariable(SettingsService.DataDirectoryVariable, custom);

            Assert.Equal(custom, SettingsService.ResolveDataDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SettingsService.DataDirectoryVariable, original);
        }
    }
}
