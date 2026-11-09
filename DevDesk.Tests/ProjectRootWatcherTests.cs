using DevDesk.Services;
using DevDesk.Models;
using Xunit;

namespace DevDesk.Tests;

public sealed class ProjectRootWatcherTests
{
    [Fact]
    public void Watcher_InvalidatesIncludedPathsButHonorsIgnoresAndSafetyBoundaries()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        Directory.CreateDirectory(root);
        var matcher = new IgnoreRuleMatcher(new[] { "bin", "!bin/keep" });

        Assert.True(ProjectRootWatcher.ShouldInvalidatePath(
            root, matcher, Path.Combine(root, "src", "App.cs")));
        Assert.False(ProjectRootWatcher.ShouldInvalidatePath(
            root, matcher, Path.Combine(root, "bin", "generated.dll")));
        Assert.True(ProjectRootWatcher.ShouldInvalidatePath(
            root, matcher, Path.Combine(root, "bin", "keep", "App.cs")));
        Assert.False(ProjectRootWatcher.ShouldInvalidatePath(
            root, matcher, Path.Combine(root, ".git", "config")));
        Assert.False(ProjectRootWatcher.ShouldInvalidatePath(
            root, matcher, Path.Combine(sandbox.Directory, "outside.txt")));
    }

    [Fact]
    public async Task Watcher_DebouncesBurstIntoOneAffectedRootRefresh()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        Directory.CreateDirectory(root);
        var engine = new FakeScanEngine
        {
            RootScan = _ => new ScannerResult(),
        };
        var catalog = new ProjectCatalog(engine);
        await catalog.RefreshAsync();
        using var watcher = new ProjectRootWatcher(catalog);
        watcher.Reconfigure(new[] { new ScanRootSettings { Path = root, Ignores = new() } });

        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(Path.Combine(root, $"change-{index}.txt"), "updated");
        }

        Assert.True(SpinWait.SpinUntil(() => engine.RootCalls > 0, TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Assert.Equal(1, engine.Calls);
        Assert.Equal(1, engine.RootCalls);
    }

    [Fact]
    public async Task Watcher_RefreshesKnownCheckoutWithoutRescanningItsRoot()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        var checkout = Path.Combine(root, "project");
        Directory.CreateDirectory(checkout);
        var projectFile = Path.Combine(checkout, "project.csproj");
        File.WriteAllText(projectFile, "<Project />");
        var original = new ProjectModel
        {
            Name = "project",
            Path = checkout,
            ScanRootPath = root,
            Branch = "old",
            Version = "1.0.0",
        };
        var updated = new ProjectModel
        {
            Name = "project",
            Path = checkout,
            ScanRootPath = root,
            Branch = "updated",
            Version = "1.0.0",
        };
        var engine = new FakeScanEngine
        {
            Projects = new[] { original },
            CheckoutScan = (_, _) => updated,
        };
        var catalog = new ProjectCatalog(engine);
        await catalog.RefreshAsync();
        using var watcher = new ProjectRootWatcher(catalog);
        watcher.Reconfigure(new[] { new ScanRootSettings { Path = root } });

        File.WriteAllText(projectFile, "<Project><PropertyGroup /></Project>");

        Assert.True(SpinWait.SpinUntil(
            () => catalog.Snapshot.Projects.Single().Branch == "updated",
            TimeSpan.FromSeconds(5)));
        Assert.Equal(1, engine.CheckoutCalls);
        Assert.Equal(0, engine.RootCalls);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task Watcher_OverflowReconcilesTheWholeRoot()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        Directory.CreateDirectory(root);
        var engine = new FakeScanEngine
        {
            RootScan = _ => new ScannerResult(),
        };
        var catalog = new ProjectCatalog(engine);
        await catalog.RefreshAsync();
        using var watcher = new ProjectRootWatcher(catalog);
        var settings = new ScanRootSettings { Path = root, Ignores = new() };
        watcher.Reconfigure(new[] { settings });
        var before = engine.Calls;

        // An overflow means events were lost, so no path can be trusted: the
        // watcher must fall back to one reconciliation scan of the whole root.
        RaiseWatcherError(
            watcher,
            settings,
            new InternalBufferOverflowException("simulated overflow"));

        Assert.True(SpinWait.SpinUntil(() => engine.RootCalls >= 1, TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(700));

        // One reconciliation scan, not a storm of them, and no second full scan.
        Assert.Equal(1, engine.RootCalls);
        Assert.Equal(before, engine.Calls);
    }

    [Fact]
    public async Task Watcher_ErrorAfterDisposeDoesNotScan()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        Directory.CreateDirectory(root);
        var engine = new FakeScanEngine
        {
            RootScan = _ => new ScannerResult(),
        };
        var catalog = new ProjectCatalog(engine);
        await catalog.RefreshAsync();
        var watcher = new ProjectRootWatcher(catalog);
        var settings = new ScanRootSettings { Path = root, Ignores = new() };
        watcher.Reconfigure(new[] { settings });
        var before = engine.Calls;

        watcher.Dispose();
        RaiseWatcherError(watcher, settings, new IOException("simulated failure"));

        await Task.Delay(TimeSpan.FromMilliseconds(900));
        Assert.Equal(before, engine.Calls);
    }

    /// <summary>
    /// Delivers the platform's watcher-failure callback. The handler is the real
    /// production one; reflection only replaces the OS event, because provoking a
    /// real FileSystemWatcher buffer overflow needs thousands of events and is not
    /// reliable on CI.
    /// </summary>
    private static void RaiseWatcherError(
        ProjectRootWatcher watcher,
        ScanRootSettings root,
        Exception error)
    {
        var handler = typeof(ProjectRootWatcher).GetMethod(
            "OnWatcherError",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(handler);
        handler!.Invoke(watcher, new object?[] { root, error });
    }
}
