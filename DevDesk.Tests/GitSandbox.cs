using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// A throw-away folder with real (tiny) git repositories in it. Every scan test
/// builds the shape it needs instead of depending on the developer's disk, and
/// nothing outside the sandbox is ever read or written.
/// </summary>
internal sealed class GitSandbox : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "devdesk-scan-" + Guid.NewGuid().ToString("N"));

    public GitSandbox() => Directory.CreateDirectory(Root);

    public string PathOf(string relative) => Path.Combine(Root, relative);

    /// <summary>An initialised repo with a local identity, so commits work offline.</summary>
    public async Task<string> InitRepoAsync(string relative = "repo")
    {
        var directory = PathOf(relative);
        Directory.CreateDirectory(directory);
        await RunOk(directory, "init");
        await RunOk(directory, "config", "user.email", "test@example.com");
        await RunOk(directory, "config", "user.name", "Test");
        await RunOk(directory, "config", "core.autocrlf", "false");
        return directory;
    }

    public Task CommitAsync(string directory, params string[] files) =>
        WriteAndCommitAsync(directory, files.Select(f => (f, "content")).ToArray());

    public async Task WriteAndCommitAsync(string directory, params (string Path, string Content)[] files)
    {
        foreach (var file in files)
        {
            var full = Path.Combine(directory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, file.Content);
        }

        await RunOk(directory, "add", "-A");
        await RunOk(directory, "commit", "-m", files[0].Path);
    }

    public Task<GitResult> RunAsync(string directory, params string[] args) =>
        GitRunner.RunAsync(directory, args);

    public async Task RunOk(string directory, params string[] args)
    {
        var result = await GitRunner.RunAsync(directory, args);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} failed: {result.StdErr}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // best-effort temp cleanup (read-only git objects can block it)
        }
    }
}
