using System;
using System.IO;
using System.Threading.Tasks;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleaseNotesDraftServiceTests
{
    [Fact]
    public async Task CreateAsync_GeneratesNotesFromCommitsAfterTheSelectedLocalTag()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("Notes repo");
        await sandbox.CommitAsync(repo, "before.txt");
        await sandbox.RunOk(repo, "tag", "-a", "v0.5.0-beta", "-m", "previous release");
        await CommitWithMessage(sandbox, repo, "feature.txt", "feat: add release preview");
        await CommitWithMessage(sandbox, repo, "fix.txt", "fix: repair release flow");
        await sandbox.RunOk(repo, "tag", "-a", "v0.5.1-beta", "-m", "next tag");

        var result = await new ReleaseNotesDraftService(new GitService())
            .CreateAsync(repo, "v0.5.0-beta");

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Draft);
        Assert.Equal("v0.5.0-beta", result.Draft.PreviousTag);
        Assert.False(result.Draft.IsTruncated);
        Assert.Equal(2, result.Draft.Commits.Count);
        Assert.Contains("## Features", result.Draft.Markdown);
        Assert.Contains("feat: add release preview", result.Draft.Markdown);
        Assert.Contains("## Fixes", result.Draft.Markdown);
        Assert.Contains("fix: repair release flow", result.Draft.Markdown);
        Assert.DoesNotContain("before.txt", result.Draft.Markdown);
        Assert.DoesNotContain("next tag", result.Draft.Markdown);
    }

    [Fact]
    public async Task CreateAsync_NoPreviousTagUsesBoundedLocalHistory()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("First release notes");
        await sandbox.CommitAsync(repo, "one.txt");
        await CommitWithMessage(sandbox, repo, "two.txt", "add second file");

        var result = await new ReleaseNotesDraftService(new GitService())
            .CreateAsync(repo, previousTag: null);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Draft!.PreviousTag);
        Assert.Equal(2, result.Draft.Commits.Count);
        Assert.Contains("## Other changes", result.Draft.Markdown);
    }

    [Fact]
    public async Task CreateAsync_EmptyRangeProducesAnEmptyDraft()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("Empty notes");
        await sandbox.CommitAsync(repo, "base.txt");
        await sandbox.RunOk(repo, "tag", "-a", "v0.5.0-beta", "-m", "release");

        var result = await new ReleaseNotesDraftService(new GitService())
            .CreateAsync(repo, "v0.5.0-beta");

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Draft!.Commits);
        Assert.Empty(result.Draft.Markdown);
    }

    [Theory]
    [InlineData("bad tag")]
    [InlineData("v0.5.0-beta..HEAD")]
    [InlineData("--upload-pack=malicious")]
    public async Task CreateAsync_RefusesInvalidOrMissingPreviousTag(string tag)
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("Invalid tag");
        await sandbox.CommitAsync(repo, "base.txt");

        var result = await new ReleaseNotesDraftService(new GitService()).CreateAsync(repo, tag);

        Assert.False(result.Succeeded);
        Assert.Null(result.Draft);
        Assert.NotEmpty(result.Error);
    }

    private static async Task CommitWithMessage(
        GitSandbox sandbox,
        string repository,
        string fileName,
        string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repository, fileName), message);
        await sandbox.RunOk(repository, "add", "--", fileName);
        await sandbox.RunOk(repository, "commit", "-m", message);
    }
}
