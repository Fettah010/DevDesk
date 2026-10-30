using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Integration-ish tests for the scan engine: each test builds real
/// (tiny) git repos in a temp folder and asserts on the scan output. Requires
/// git on PATH (it is, on the dev machine).
/// </summary>
public class ScanEngineBaselineTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Scan_FindsOtherRepos_AndReturnsSelfAsWorkspace()
    {
        var self = await InitRepoAsync(_tmp.Path, "RepoA");
        var other = await InitRepoAsync(_tmp.Path, "RepoB");
        await CommitAsync(self, "readme.txt");
        await CommitAsync(other, "readme.txt");

        var result = await Scan(_tmp.Path, self);

        var project = Assert.Single(result.Projects);
        Assert.Equal("RepoB", project.Name);

        Assert.NotNull(result.Self);
        Assert.Equal("RepoA", result.Self.Name);
        Assert.True(result.Self.IsWorkspace);
        Assert.Equal(self, result.Self.Path);
    }

    [Fact]
    public async Task Scan_StopsInsideRepos_SkipsIgnoredAndPlainFolders()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Repo");
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        Directory.CreateDirectory(Path.Combine(repo, "Nested"));
        File.WriteAllText(Path.Combine(repo, "Nested", "file.cs"), "x");
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "plain"));
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "Logs"));
        await CommitAsync(repo, "readme.txt");

        var result = await ScanRoot(_tmp.Path);

        var project = Assert.Single(result.Projects);
        Assert.Equal("Repo", project.Name);
        Assert.DoesNotContain(result.Projects, p => p.Name == "Nested");
        Assert.DoesNotContain(result.Projects, p => p.Name == "plain");
    }

    [Fact]
    public async Task Scan_DetectsCsprojProjectByMarker_EvenWithoutGit()
    {
        var dir = Path.Combine(_tmp.Path, "NoGitYet");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = await ScanRoot(_tmp.Path);

        Assert.Contains(result.Projects, p => p.Name == "NoGitYet");
    }

    [Fact]
    public async Task Scan_ReadsVersion_AndFlagsSyncMismatch()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Tool");
        await WriteAndCommitAsync(repo, "Tool.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<Version>1.2.3</Version><AssemblyVersion>1.2.3.0</AssemblyVersion><FileVersion>1.2.4</FileVersion>" +
            "</PropertyGroup></Project>");

        var result = await ScanRoot(_tmp.Path);

        var project = Assert.Single(result.Projects);
        Assert.Equal("Tool", project.Name);
        Assert.Equal("1.2.3", project.Version);
        Assert.True(project.VersionMismatch);
    }

    [Fact]
    public async Task Scan_SyncedVersionFields_AreNotFlagged()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Tool");
        await WriteAndCommitAsync(repo, "Tool.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<Version>1.2.3</Version><AssemblyVersion>1.2.3</AssemblyVersion><FileVersion>1.2.3</FileVersion>" +
            "</PropertyGroup></Project>");

        var result = await ScanRoot(_tmp.Path);

        Assert.False(Assert.Single(result.Projects).VersionMismatch);
    }

    [Fact]
    public async Task Scan_ReadsRecentCommits_NewestFirst()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Repo");
        await CommitAsync(repo, "first.txt");
        await CommitAsync(repo, "second.txt");

        var result = await ScanRoot(_tmp.Path);

        var project = Assert.Single(result.Projects);
        Assert.Equal(2, project.RecentCommits.Count);
        Assert.Equal("second.txt", project.RecentCommits[0].Subject);
        Assert.All(project.RecentCommits, c => Assert.False(string.IsNullOrWhiteSpace(c.Hash)));
    }

    [Fact]
    public async Task Scan_DetectsDirtyWorkingTree()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Repo");
        await CommitAsync(repo, "clean.txt");

        var before = await ScanRoot(_tmp.Path);
        Assert.False(Assert.Single(before.Projects).Dirty);

        File.AppendAllText(Path.Combine(repo, "clean.txt"), "more");
        var after = await ScanRoot(_tmp.Path);
        Assert.True(Assert.Single(after.Projects).Dirty);
    }

    [Fact]
    public async Task Scan_DetectsBehindUpstreamViaRemoteTracking()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Local");
        await CommitAsync(repo, "one.txt");

        var bare = Path.Combine(_tmp.Path, "remote.git");
        Directory.CreateDirectory(bare);
        await RunAssertOk(bare, "init", "--bare");
        await RunAssertOk(repo, "remote", "add", "origin", bare);
        await RunAssertOk(repo, "push", "-u", "origin", "HEAD");

        var clone = await CloneAsync(_tmp.Path, "remote.git", "Clone");
        await CommitAsync(clone, "two.txt");
        await RunAssertOk(clone, "push", "origin", "HEAD");
        await RunAssertOk(repo, "fetch");

        var result = await Scan(_tmp.Path, repo);

        Assert.NotNull(result.Self);
        Assert.NotEmpty(result.Self.Branch);
        Assert.NotEqual("detached", result.Self.Branch);
        Assert.True(result.Self.BehindUpstream);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "GitHub")]
    [InlineData("git@github.com:owner/repo.git", "GitHub")]
    [InlineData("https://gitlab.example.com/team/repo.git", "Other")]
    public async Task Scan_ClassifiesRemoteHost(string url, string expectedHost)
    {
        var repo = await InitRepoAsync(_tmp.Path, "Repo");
        await CommitAsync(repo, "readme.txt");
        await RunAssertOk(repo, "remote", "add", "origin", url);

        var result = await ScanRoot(_tmp.Path);

        var project = Assert.Single(result.Projects);
        Assert.True(project.HasRemote);
        Assert.True(project.RemoteOk);
        Assert.Equal(expectedHost, project.RemoteHost);
    }

    [Fact]
    public async Task Scan_LocalOnlyRepo_HasNoRemote()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Repo");
        await CommitAsync(repo, "readme.txt");

        var result = await ScanRoot(_tmp.Path);

        var project = Assert.Single(result.Projects);
        Assert.False(project.HasRemote);
        Assert.False(project.RemoteOk);
        Assert.Equal("Local only", project.RemoteHost);
        Assert.Equal("No remote configured", project.StatusText);
    }

    [Fact]
    public async Task Scan_FlagsMultiProjectRepoAsWorkspace()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Suit");
        await CommitAsync(repo, "App1/App1.csproj");
        await CommitAsync(repo, "App2/App2.csproj");

        var result = await ScanRoot(_tmp.Path);

        Assert.True(Assert.Single(result.Projects).IsWorkspace);
    }

    [Fact]
    public async Task Scan_HandlesEmptyRepo_WithoutCrashing()
    {
        var repo = await InitRepoAsync(_tmp.Path, "Empty");

        var result = await ScanRoot(_tmp.Path);

        var project = Assert.Single(result.Projects);
        Assert.Empty(project.RecentCommits);
        Assert.False(project.Dirty);
        Assert.False(string.IsNullOrWhiteSpace(project.Branch));
    }

    [Fact]
    public async Task Scan_SelfRepo_GetsComponents()
    {
        var self = await InitRepoAsync(_tmp.Path, "SelfRepo");
        await CommitAsync(
            self,
            "SelfRepo.csproj",
            "DevDesk.Tests/DevDesk.Tests.csproj",
            "docs/DEVDESK-PLAN.md",
            "Scripts/launch-dev.ps1");
        var other = await InitRepoAsync(_tmp.Path, "Other");
        await CommitAsync(other, "readme.txt");

        var result = await Scan(_tmp.Path, self);

        Assert.Contains(result.Projects, p => p.Name == "Other");
        Assert.DoesNotContain(result.Projects, p => p.Name == "SelfRepo");

        Assert.NotNull(result.Self);
        Assert.Contains(result.Self.Components, c => c.Kind == "app");
        Assert.Contains(result.Self.Components, c => c.Kind == "tests");
        Assert.Contains(result.Self.Components, c => c.Kind == "docs");
        Assert.Contains(result.Self.Components, c => c.Kind == "scripts");
    }

    [Fact]
    public async Task Scan_RealEcosystem_YieldsTemplate_AndNotSelf()
    {
        if (!Directory.Exists(ScanEngine.DefaultScanRoot))
        {
            return;
        }

        var result = await new ScanEngine().ScanAsync();

        Assert.NotEmpty(result.Projects);
        Assert.DoesNotContain(result.Projects, p => p.Name == "DevDesk");
        Assert.NotNull(result.Self);
        Assert.Contains(result.Projects, p => p.Name == "DevTem Win UI 3" || p.Name == "DevTem WinUI 3");
    }

    private Task<ScannerResult> Scan(string root, string selfPath) =>
        new ScanEngine(root, selfPath).ScanAsync();

    private Task<ScannerResult> ScanRoot(string root) =>
        new ScanEngine(root, Path.Combine(root, "_no-such-self_")).ScanAsync();

    private static async Task<string> InitRepoAsync(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        await RunAssertOk(dir, "init");
        await RunAssertOk(dir, "config", "user.email", "test@example.com");
        await RunAssertOk(dir, "config", "user.name", "Test");
        return dir;
    }

    private static async Task<string> CloneAsync(string root, string remoteName, string name)
    {
        var dir = Path.Combine(root, name);
        await RunAssertOk(root, "clone", Path.Combine(root, remoteName), name);
        await RunAssertOk(dir, "config", "user.email", "test@example.com");
        await RunAssertOk(dir, "config", "user.name", "Test");
        return dir;
    }

    private static async Task CommitAsync(string dir, params string[] files)
    {
        await WriteFilesAndCommitAsync(dir, files.Select(f => (f, "content")).ToArray());
    }

    private static async Task WriteAndCommitAsync(string dir, string relativePath, string content)
    {
        await WriteFilesAndCommitAsync(dir, new[] { (relativePath, content) });
    }

    private static async Task WriteFilesAndCommitAsync(string dir, params (string Path, string Content)[] files)
    {
        foreach (var file in files)
        {
            var full = Path.Combine(dir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, file.Content);
        }

        await RunAssertOk(dir, "add", "-A");
        await RunAssertOk(dir, "commit", "-m", files[0].Path);
    }

    private static async Task RunAssertOk(string dir, params string[] args)
    {
        var result = await GitRunner.RunAsync(dir, args);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} failed: {result.StdErr}");
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devdesk-scan-" + Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // temp cleanup is best-effort
            }
        }
    }
}

/// <summary>Kept from the pre-scanner suite: glyph mapping is pure model logic.</summary>
public class ComponentGlyphTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("tests")]
    [InlineData("docs")]
    [InlineData("scripts")]
    [InlineData("workflow")]
    public void Glyphs_AreMappedForEveryKnownKind(string kind)
    {
        var component = new ComponentInfo { Name = "X", RelativePath = "X", Kind = kind };
        Assert.False(string.IsNullOrWhiteSpace(component.Glyph));
    }
}