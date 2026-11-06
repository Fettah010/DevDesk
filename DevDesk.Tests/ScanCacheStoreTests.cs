using System;
using System.IO;
using System.Linq;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ScanCacheStoreTests
{
    [Fact]
    public void ScanCache_RoundTripsSnapshotAsCachedData()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        var projectPath = Path.Combine(root, "Project");
        Directory.CreateDirectory(projectPath);
        var store = NewStore(sandbox);
        var project = new ProjectModel
        {
            Name = "Project",
            Path = projectPath,
            RepositoryIdentity = Path.Combine(projectPath, ".git"),
            IsWorktree = true,
            WorktreeOfPath = Path.Combine(root, "Main"),
            Branch = "feature",
            Version = "2.3.4",
            HasRemote = false,
            RemoteOk = false,
            RemoteHost = "Local only",
            Dirty = true,
            BehindUpstream = false,
            StatusText = "Uncommitted changes",
            RecentCommits = new[]
            {
                new CommitInfo { Hash = "abc123", Subject = "feature", Author = "Author", Date = "today" },
            },
            Components = new[]
            {
                new ComponentInfo { Name = "Tests", RelativePath = @"Tests\Tests.csproj", Kind = "tests" },
            },
        };
        var capturedAt = DateTimeOffset.UtcNow;
        store.Save(new CatalogSnapshot(
            new[] { project },
            null,
            capturedAt: capturedAt,
            duration: TimeSpan.FromSeconds(2)));

        var loaded = store.Load(new[] { new ScanRootSettings { Path = root } });

        var snapshot = Assert.IsType<CatalogSnapshot>(loaded);
        var cached = Assert.Single(snapshot.Projects);
        Assert.Equal(CatalogFreshness.Cached, snapshot.Freshness);
        Assert.Equal(capturedAt, snapshot.CapturedAt);
        Assert.Equal(project.RepositoryIdentity, cached.RepositoryIdentity);
        Assert.True(cached.IsWorktree);
        Assert.Equal("feature", Assert.Single(cached.RecentCommits).Subject);
        Assert.Equal("Tests", Assert.Single(cached.Components).Name);
    }

    [Fact]
    public void ScanCache_DoesNotLoadProjectsFromDisabledRoots()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        var projectPath = Path.Combine(root, "Project");
        Directory.CreateDirectory(projectPath);
        var store = NewStore(sandbox);
        store.Save(new CatalogSnapshot(new[]
        {
            new ProjectModel
            {
                Name = "Project",
                Path = projectPath,
                Branch = "main",
                Version = "—",
                HasRemote = false,
                RemoteOk = false,
                RemoteHost = "Local only",
            },
        }, null));

        var loaded = store.Load(new[] { new ScanRootSettings { Path = root, Enabled = false } });

        Assert.Empty(Assert.IsType<CatalogSnapshot>(loaded).Projects);
    }

    [Fact]
    public void ScanCache_UnsupportedSchemaIsIgnored()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        Directory.CreateDirectory(root);
        var store = NewStore(sandbox);
        new JsonStore(store.CachePath).Save(new System.Text.Json.Nodes.JsonObject
        {
            ["schemaVersion"] = ScanCacheStore.SchemaVersion + 1,
        });

        Assert.Null(store.Load(new[] { new ScanRootSettings { Path = root } }));
    }

    [Fact]
    public void ScanCache_CorruptDocumentIsBackedUpAndIgnored()
    {
        using var sandbox = new SettingsSandbox();
        var store = NewStore(sandbox);
        Directory.CreateDirectory(Path.GetDirectoryName(store.CachePath)!);
        File.WriteAllText(store.CachePath, "{ broken");

        Assert.Null(store.Load(new[] { new ScanRootSettings() }));
        Assert.Contains(
            Directory.EnumerateFiles(sandbox.Directory),
            path => Path.GetFileName(path).Contains(".corrupt-", StringComparison.Ordinal));
    }

    private static ScanCacheStore NewStore(SettingsSandbox sandbox) =>
        new(new JsonStore(Path.Combine(sandbox.Directory, "scan-cache.json")));
}
