using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The runner is the one place that shells out to git, so its safety promises
/// are tested here: it never prompts, it never throws on a git failure, and it
/// does not mangle the output it hands to the parsers.
/// </summary>
public class GitRunnerTests
{
    [Fact]
    public void GitRunner_NeverPromptsForCredentials()
    {
        var environment = GitRunner.NonInteractiveEnvironment;

        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", environment["GCM_INTERACTIVE"]);
        Assert.Equal("0", environment["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal("cat", environment["GIT_PAGER"]);
    }

    [Fact]
    public async Task GitRunner_UnreachableRemote_FailsFastInsteadOfHanging()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("Repo");

        var stopwatch = Stopwatch.StartNew();
        var result = await GitRunner.RunAsync(repo, "ls-remote", "https://127.0.0.1:1/repo.git");
        stopwatch.Stop();

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Username for", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not read Username", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12),
            $"git hung for {stopwatch.ElapsedMilliseconds} ms on an unreachable remote");
    }

    [Fact]
    public async Task GitRunner_GitFailureReturnsExitCode_DoesNotThrow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "devdesk-notarepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var result = await GitRunner.RunAsync(directory, "status", "--porcelain=v2", "--branch");

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.StdErr));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task GitRunner_ReportsVersion()
    {
        using var sandbox = new GitSandbox();

        var result = await GitRunner.RunAsync(sandbox.Root, "--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void GitRunner_Normalize_KeepsPorcelainColumns()
    {
        var raw = "# branch.head main\r\n? leading space is data\r\n\r\n";

        var normalized = GitRunner.Normalize(raw);

        Assert.Equal("# branch.head main\n? leading space is data", normalized);
        Assert.DoesNotContain('\r', normalized);
        Assert.DoesNotContain("\n\n", normalized);
    }
}
