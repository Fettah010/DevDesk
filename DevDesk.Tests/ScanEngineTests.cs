using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;
using Xunit.Abstractions;

namespace DevDesk.Tests;

/// <summary>
/// The correctness half of the scan: what counts as a repo, what must never be
/// walked, how the self checkout is found, determinism, read-only behaviour and
/// cancellation. Each test builds the shape it needs in a sandbox.
/// </summary>
public class ScanEngineTests
{
    private readonly ITestOutputHelper _output;

    public ScanEngineTests(ITestOutputHelper output) => _output = output;

    // --- repo detection -----------------------------------------------------

    [Fact]
    public async Task Scan_DetectsGitFileWorktree()
    {
        using var sandbox = new GitSandbox();
        var main = await sandbox.InitRepoAsync("main");
        await sandbox.CommitAsync(main, "readme.txt");

        var worktree = sandbox.PathOf(Path.Combine("checked-out", "feature"));
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        await sandbox.RunOk(main, "worktree", "add", worktree, "-b", "feature");

        // The case under test: a worktree's .git is a FILE (gitdir: pointer).
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));

        var result = await Scan(sandbox.Root);

        var project = Assert.Single(result.Projects, p => p.Name == "feature");
        Assert.Equal("feature", project.Branch);
        Assert.NotEqual("—", project.Branch);
        Assert.False(project.Dirty);
    }

    [Fact]
    public async Task Scan_MarkerFolderWithoutGit_IsNotCalledDetached()
    {
        using var sandbox = new GitSandbox();
        var directory = sandbox.PathOf("Plain");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Plain.csproj"), "<Project />");

        var result = await Scan(sandbox.Root);

        var project = Assert.Single(result.Projects);
        Assert.Equal("—", project.Branch);
        Assert.Equal("Not a git repository", project.StatusText);
        Assert.False(project.HasRemote);
    }

    // --- hygiene ------------------------------------------------------------

    [Fact]
    public async Task Scan_SkipsHygieneFolders()
    {
        using var sandbox = new GitSandbox();
        foreach (var hygiene in new[] { "bin", "obj", "Logs", "Releases", "node_modules", ".hidden" })
        {
            await sandbox.InitRepoAsync(Path.Combine(hygiene, "Inside"));
        }

        var visible = await sandbox.InitRepoAsync("Visible");
        await sandbox.CommitAsync(visible, "readme.txt");

        var result = await Scan(sandbox.Root);

        var project = Assert.Single(result.Projects);
        Assert.Equal("Visible", project.Name);
    }

    [Fact]
    public async Task Scan_IgnoredFolderYieldsNoCards()
    {
        using var sandbox = new GitSandbox();
        var project = await sandbox.InitRepoAsync("Project");
        await sandbox.CommitAsync(project, "readme.txt");

        // A real worktree, one level below an ignored folder: without D8 this
        // would be a second card for a project that already has one.
        var worktree = sandbox.PathOf(Path.Combine("copilot-worktrees", "Project", "wt"));
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        await sandbox.RunOk(project, "worktree", "add", worktree, "-b", "wt");
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));

        var result = await Scan(sandbox.Root);

        Assert.Single(result.Projects);
        Assert.DoesNotContain(result.Projects,
            p => p.Path.Contains("copilot-worktrees", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scan_SkipsReparsePoints()
    {
        using var sandbox = new GitSandbox();
        var real = await sandbox.InitRepoAsync("real");
        await sandbox.CommitAsync(real, "readme.txt");

        var link = sandbox.PathOf("linked");
        if (!TryCreateDirectoryLink(link, real))
        {
            return; // this machine refuses to create links without elevation
        }

        var result = await Scan(sandbox.Root);

        var project = Assert.Single(result.Projects);
        Assert.Equal("real", project.Name);
    }

    // --- self discovery -----------------------------------------------------

    [Fact]
    public async Task Scan_SelfFoundWhenFolderRenamed()
    {
        using var sandbox = new GitSandbox();
        var checkout = await sandbox.InitRepoAsync("RenamedCheckout");
        var deep = Path.Combine(checkout, "bin", "x64", "Debug", "net10.0-windows");
        Directory.CreateDirectory(deep);

        var found = SelfLocator.FindSourceRepo(deep, sandbox.PathOf("no-local-appdata"));

        Assert.Equal(PathHygiene.Canonicalize(checkout), found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_SelfNullWhenInstalled()
    {
        using var sandbox = new GitSandbox();
        var localAppData = sandbox.PathOf("localappdata");
        var installed = Path.Combine(localAppData, "DevDesk", "current");
        Directory.CreateDirectory(Path.Combine(installed, ".git"));
        var bin = Path.Combine(installed, "bin");
        Directory.CreateDirectory(bin);

        // Even a stray .git under the install root is not a source checkout.
        Assert.Null(SelfLocator.FindSourceRepo(bin, localAppData));
    }

    [Fact]
    public async Task Scan_SelfPath_IsExcludedFromTheProjectList()
    {
        using var sandbox = new GitSandbox();
        var self = await sandbox.InitRepoAsync("MyCheckout");
        await sandbox.CommitAsync(self, "self.txt");
        var other = await sandbox.InitRepoAsync("Other");
        await sandbox.CommitAsync(other, "other.txt");

        var result = await new ScanEngine(sandbox.Root, self).ScanAsync();

        Assert.Single(result.Projects);
        Assert.Equal("Other", result.Projects[0].Name);
        Assert.NotNull(result.Self);
        Assert.Equal("MyCheckout", result.Self!.Name);
        Assert.True(result.Self.IsWorkspace);
    }

    // --- determinism --------------------------------------------------------

    [Fact]
    public async Task Scan_ProjectFileIsDeterministic()
    {
        using var sandbox = new GitSandbox();

        // No file named after the folder: the alphabetically first one wins.
        var alpha = await sandbox.InitRepoAsync("Alpha");
        await sandbox.WriteAndCommitAsync(
            alpha,
            ("zeta.csproj", "<Project><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>"),
            ("beta.csproj", "<Project><PropertyGroup><Version>1.1.1</Version></PropertyGroup></Project>"));

        // A file named after the folder wins over alphabetical order.
        var tool = await sandbox.InitRepoAsync("Tool");
        await sandbox.WriteAndCommitAsync(
            tool,
            ("A.csproj", "<Project><PropertyGroup><Version>0.0.1</Version></PropertyGroup></Project>"),
            ("Tool.csproj", "<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>"));

        var first = await Scan(sandbox.Root);
        var second = await Scan(sandbox.Root);

        Assert.Equal("1.1.1", Assert.Single(first.Projects, p => p.Name == "Alpha").Version);
        Assert.Equal("2.0.0", Assert.Single(first.Projects, p => p.Name == "Tool").Version);
        Assert.Equal(
            first.Projects.Select(p => p.Name),
            second.Projects.Select(p => p.Name));
        Assert.Equal(
            first.Projects.Select(p => p.Version),
            second.Projects.Select(p => p.Version));
    }

    [Fact]
    public void SelectProjectFile_PrefersTheFolderName_ThenAlphabeticalOrder()
    {
        var files = new[]
        {
            @"C:\repo\zeta.csproj",
            @"C:\repo\Beta.csproj",
            @"C:\repo\Beta.cs",
        };

        Assert.Equal(@"C:\repo\Beta.csproj", ScanEngine.SelectProjectFile(files, "repo"));
        Assert.Equal(@"C:\repo\Beta.csproj", ScanEngine.SelectProjectFile(files.Reverse(), "repo"));
        Assert.Equal(@"C:\repo\zeta.csproj", ScanEngine.SelectProjectFile(files, "Zeta"));
        Assert.Null(ScanEngine.SelectProjectFile(new[] { @"C:\repo\readme.md" }, "repo"));
    }

    // --- parsing ------------------------------------------------------------

    [Fact]
    public void Scan_PorcelainV2ParsesCrLf()
    {
        const string stdout =
            "# branch.oid 1234567890abcdef\r\n" +
            "# branch.head main\r\n" +
            "# branch.upstream origin/main\r\n" +
            "# branch.ab +2 -3\r\n" +
            "1 .M N... 100644 100644 100644 abcdef abcdef file.txt\r\n" +
            "? untracked.txt\r\n";

        var status = ScanEngine.ParseStatus(stdout);

        Assert.Equal("main", status.Branch);
        Assert.False(status.Detached);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(3, status.Behind);
        Assert.True(status.Dirty);
    }

    [Fact]
    public void Scan_PorcelainV2_HandlesCleanUnbornAndDetached()
    {
        var clean = ScanEngine.ParseStatus("# branch.oid abc\n# branch.head main\n# branch.ab +0 -0\n");
        Assert.Equal("main", clean.Branch);
        Assert.False(clean.Dirty);
        Assert.Equal(0, clean.Behind);

        var unborn = ScanEngine.ParseStatus("# branch.oid (initial)\n# branch.head trunk\n");
        Assert.Equal("trunk", unborn.Branch);
        Assert.True(unborn.Unborn);
        Assert.False(unborn.Dirty);

        var detached = ScanEngine.ParseStatus("# branch.oid abc\n# branch.head (detached)\n");
        Assert.True(detached.Detached);
        Assert.Equal(string.Empty, detached.Branch);
    }

    [Fact]
    public void Scan_PorcelainV2_IgnoredFilesAreNotDirt()
    {
        var status = ScanEngine.ParseStatus("# branch.head main\n! build/output.bin\n");

        Assert.False(status.Dirty);
    }

    // --- behaviour ----------------------------------------------------------

    [Fact]
    public async Task Scan_IsReadOnly()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("Repo");
        await sandbox.WriteAndCommitAsync(
            repo,
            ("Repo.csproj", "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>"));

        var index = Path.Combine(repo, ".git", "index");
        var projectFile = Path.Combine(repo, "Repo.csproj");
        var indexBefore = File.GetLastWriteTimeUtc(index);
        var projectBefore = File.GetLastWriteTimeUtc(projectFile);

        Thread.Sleep(1100); // so a rewrite would be visible even on a coarse clock

        var result = await Scan(sandbox.Root);

        Assert.False(Assert.Single(result.Projects).Dirty);
        Assert.Equal(indexBefore, File.GetLastWriteTimeUtc(index));
        Assert.Equal(projectBefore, File.GetLastWriteTimeUtc(projectFile));
    }

    [Fact]
    public async Task Scan_CancellationReturnsPromptly()
    {
        using var sandbox = new GitSandbox();
        for (var i = 1; i <= 8; i++)
        {
            var repo = await sandbox.InitRepoAsync($"Repo{i}");
            await sandbox.CommitAsync(repo, "readme.txt");
        }

        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var scan = Scan(sandbox.Root, cancellation.Token);

        cancellation.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        stopwatch.Stop();

        _output.WriteLine($"cancelled after {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"cancellation took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Scan_ProgressReportsCompletedAndTotal()
    {
        using var sandbox = new GitSandbox();
        for (var i = 1; i <= 3; i++)
        {
            var repo = await sandbox.InitRepoAsync($"Repo{i}");
            await sandbox.CommitAsync(repo, "readme.txt");
        }

        var reports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(reports.Add);

        var result = await new ScanEngine(sandbox.Root, sandbox.PathOf("_no-self_"))
            .ScanAsync(CancellationToken.None, progress);

        Assert.Equal(3, result.Projects.Count);

        // Progress<T> posts asynchronously; wait for the final report instead
        // of stopping after whichever worker happens to report first.
        for (var attempt = 0; attempt < 100 && !reports.Any(report => report.Total == 3 && report.Completed == 3); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Contains(reports, r => r.Total == 3 && r.Completed == 3);
        Assert.All(reports.Where(r => r.Total > 0), r => Assert.InRange(r.Completed, 1, r.Total));
        Assert.All(reports, r => Assert.False(string.IsNullOrWhiteSpace(r.CurrentName)));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Scan_RealEcosystem_ReportsDuration()
    {
        if (!Directory.Exists(ScanEngine.DefaultScanRoot))
        {
            return; // CI has no ecosystem folder
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await new ScanEngine().ScanAsync();
        stopwatch.Stop();

        _output.WriteLine(
            $"real scan: {stopwatch.ElapsedMilliseconds} ms for {result.Projects.Count} project(s)");
        _output.WriteLine("  " + string.Join(", ", result.Projects.Select(p => p.Name)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"real scan took {stopwatch.ElapsedMilliseconds} ms");
    }

    // --- helpers ------------------------------------------------------------

    private static Task<ScannerResult> Scan(string root) => Scan(root, CancellationToken.None);

    private static Task<ScannerResult> Scan(string root, CancellationToken cancellationToken) =>
        new ScanEngine(root, Path.Combine(root, "_no-self_")).ScanAsync(cancellationToken);

    /// <summary>Junction/symlink, best effort: false means "skip this test here".</summary>
    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Developer Mode is off: a junction needs no elevation for directories.
        }

        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(targetPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10000);
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }
}
