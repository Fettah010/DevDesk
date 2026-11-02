using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleaseGitExecutionServiceTests
{
    [Fact]
    public async Task CommitVersionAndCreateTagConfirmedAsync_CommitsOnlyThePreviewedFileAndCreatesLocalTag()
    {
        using var sandbox = new GitSandbox();
        var repo = await sandbox.InitRepoAsync("Release repo");
        var project = Path.Combine(repo, "DevDesk.csproj");
        await File.WriteAllTextAsync(project, ProjectXml("0.6.0", "0.6.0-beta"));
        await sandbox.RunOk(repo, "add", "--all");
        await sandbox.RunOk(repo, "commit", "-m", "baseline");
        var preview = CreatePreview(project);
        await File.WriteAllTextAsync(project, ProjectXml("0.6.1", "0.6.1-beta"));
        var service = new ReleaseGitExecutionService(new GitService());

        var result = await service.CommitVersionAndCreateTagConfirmedAsync(
            repo,
            preview,
            "Release v0.6.1-beta",
            confirmed: true);
        var status = await new GitService().GetStatusAsync(repo);
        var tags = await new GitService().GetTagsAsync(repo);
        var committedFiles = await sandbox.RunAsync(repo, "show", "--format=", "--name-only", "HEAD");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.VersionCommitted);
        Assert.True(result.TagCreated);
        Assert.Matches("^[0-9a-f]{40,64}$", result.CommitHash);
        Assert.Empty(status.Value!.Files);
        Assert.Equal("DevDesk.csproj", committedFiles.StdOut.Trim());
        Assert.Contains("v0.6.1-beta", tags.Value!.Select(tag => tag.Name));
    }

    [Fact]
    public async Task CommitReleasePlanConfirmedAsync_CommitsVersionAndConfirmedNotesTogether()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var project = Path.Combine(repo, "DevDesk.csproj");
        var preview = CreatePreview(project);
        var notesPath = ReleaseNotesSaveService.DefaultTargetPath(repo);
        const string notes = "## Features\n\n- A reviewed release note\n";
        var savedNotes = new ReleaseNotesSaveService().SaveConfirmed(
            repo,
            notesPath,
            notes,
            confirmed: true,
            overwriteConfirmed: false);
        Assert.True(savedNotes.Succeeded, savedNotes.Message);
        Assert.True(ReleasePlanBuilder.TryCreate(
            repo,
            new GitHubRepositoryIdentity("github.com", "owner", "repo"),
            new GitRemoteInfo("origin", "https://github.com/owner/repo.git", "push"),
            preview,
            notesPath,
            notes,
            commitMessage: null,
            out var plan,
            out var planError), planError);
        await File.WriteAllTextAsync(project, ProjectXml("0.6.1", "0.6.1-beta"));

        var result = await new ReleaseGitExecutionService(new GitService())
            .CommitVersionAndCreateTagConfirmedAsync(plan!, confirmed: true);
        var committedFiles = await sandbox.RunAsync(repo, "show", "--format=", "--name-only", "HEAD");
        var committedNotes = await sandbox.RunAsync(repo, "show", "HEAD:ReleaseNotes.md");
        var status = await new GitService().GetStatusAsync(repo);
        var tags = await new GitService().GetTagsAsync(repo);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.VersionCommitted);
        Assert.True(result.TagCreated);
        Assert.Contains("DevDesk.csproj", committedFiles.StdOut);
        Assert.Contains("ReleaseNotes.md", committedFiles.StdOut);
        Assert.Equal(notes.TrimEnd(), committedNotes.StdOut.TrimEnd());
        Assert.Empty(status.Value!.Files);
        Assert.Contains("v0.6.1-beta", tags.Value!.Select(tag => tag.Name));
    }

    [Fact]
    public async Task CommitReleasePlanConfirmedAsync_RefusesNotesChangedAfterPlan()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var project = Path.Combine(repo, "DevDesk.csproj");
        var preview = CreatePreview(project);
        var notesPath = ReleaseNotesSaveService.DefaultTargetPath(repo);
        const string notes = "## Features\n\n- Confirmed note\n";
        Assert.True(new ReleaseNotesSaveService().SaveConfirmed(
            repo,
            notesPath,
            notes,
            confirmed: true,
            overwriteConfirmed: false).Succeeded);
        Assert.True(ReleasePlanBuilder.TryCreate(
            repo,
            new GitHubRepositoryIdentity("github.com", "owner", "repo"),
            new GitRemoteInfo("origin", "https://github.com/owner/repo.git", "push"),
            preview,
            notesPath,
            notes,
            null,
            out var plan,
            out var planError), planError);
        await File.WriteAllTextAsync(project, ProjectXml("0.6.1", "0.6.1-beta"));
        await File.WriteAllTextAsync(notesPath, "Changed after plan");

        var result = await new ReleaseGitExecutionService(new GitService())
            .CommitVersionAndCreateTagConfirmedAsync(plan!, confirmed: true);
        var history = await new GitService().GetHistoryAsync(repo);
        var tags = await new GitService().GetTagsAsync(repo);

        Assert.False(result.Succeeded);
        Assert.Contains("changed after the release plan", result.Message);
        Assert.Single(history.Value!);
        Assert.DoesNotContain("v0.6.1-beta", tags.Value!.Select(tag => tag.Name));
    }

    [Fact]
    public async Task CommitVersionAndCreateTagConfirmedAsync_DeclineDoesNotChangeRepository()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var project = Path.Combine(repo, "DevDesk.csproj");
        var preview = CreatePreview(project);
        await File.WriteAllTextAsync(project, ProjectXml("0.6.1", "0.6.1-beta"));

        var result = await new ReleaseGitExecutionService(new GitService())
            .CommitVersionAndCreateTagConfirmedAsync(repo, preview, "release", confirmed: false);
        var status = await new GitService().GetStatusAsync(repo);
        var history = await new GitService().GetHistoryAsync(repo);

        Assert.False(result.Succeeded);
        Assert.False(result.VersionCommitted);
        Assert.Single(history.Value!);
        Assert.Single(status.Value!.Files);
        Assert.Equal(".", status.Value.Files[0].IndexStatus);
    }

    [Fact]
    public async Task CommitVersionAndCreateTagConfirmedAsync_RefusesOtherChangesBeforeStaging()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var project = Path.Combine(repo, "DevDesk.csproj");
        var preview = CreatePreview(project);
        await File.WriteAllTextAsync(project, ProjectXml("0.6.1", "0.6.1-beta"));
        await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "unrelated");

        var result = await new ReleaseGitExecutionService(new GitService())
            .CommitVersionAndCreateTagConfirmedAsync(repo, preview, "release", confirmed: true);
        var status = await new GitService().GetStatusAsync(repo);

        Assert.False(result.Succeeded);
        Assert.Contains("Only the previewed", result.Message);
        Assert.Contains(status.Value!.Files, file =>
            file.Path == "DevDesk.csproj" && file.IndexStatus == ".");
        Assert.Contains(status.Value.Files, file =>
            file.Path == "other.txt" && file.IndexStatus == "?");
    }

    [Fact]
    public async Task CommitVersionAndCreateTagConfirmedAsync_RefusesProjectVersionDifferentFromPreview()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var project = Path.Combine(repo, "DevDesk.csproj");
        var preview = CreatePreview(project);
        await File.WriteAllTextAsync(project, ProjectXml("0.6.2", "0.6.2-beta"));

        var result = await new ReleaseGitExecutionService(new GitService())
            .CommitVersionAndCreateTagConfirmedAsync(repo, preview, "release", confirmed: true);
        var history = await new GitService().GetHistoryAsync(repo);
        var status = await new GitService().GetStatusAsync(repo);

        Assert.False(result.Succeeded);
        Assert.Contains("do not match the confirmed preview", result.Message);
        Assert.Single(history.Value!);
        Assert.Single(status.Value!.Files);
        Assert.DoesNotContain("v0.6.1-beta", (await new GitService().GetTagsAsync(repo)).Value!
            .Select(tag => tag.Name));
    }

    [Fact]
    public async Task CommitVersionAndCreateTagConfirmedAsync_RefusesPreStagedChanges()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var project = Path.Combine(repo, "DevDesk.csproj");
        var preview = CreatePreview(project);
        await File.WriteAllTextAsync(project, ProjectXml("0.6.1", "0.6.1-beta"));
        await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "staged unrelated");
        await sandbox.RunOk(repo, "add", "other.txt");

        var result = await new ReleaseGitExecutionService(new GitService())
            .CommitVersionAndCreateTagConfirmedAsync(repo, preview, "release", confirmed: true);
        var status = await new GitService().GetStatusAsync(repo);

        Assert.False(result.Succeeded);
        Assert.Contains("Only the previewed", result.Message);
        Assert.Contains(status.Value!.Files, file => file.IndexStatus != ".");
        Assert.DoesNotContain("v0.6.1-beta", (await new GitService().GetTagsAsync(repo)).Value!.Select(tag => tag.Name));
    }

    [Fact]
    public async Task CommitVersionAndCreateTagConfirmedAsync_RefusesExistingLocalTag()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        await sandbox.RunOk(repo, "tag", "v0.6.1-beta");
        var project = Path.Combine(repo, "DevDesk.csproj");
        var preview = CreatePreview(project);
        await File.WriteAllTextAsync(project, ProjectXml("0.6.1", "0.6.1-beta"));

        var result = await new ReleaseGitExecutionService(new GitService())
            .CommitVersionAndCreateTagConfirmedAsync(
                repo,
                preview,
                "release",
                confirmed: true);
        var history = await new GitService().GetHistoryAsync(repo);
        var status = await new GitService().GetStatusAsync(repo);

        Assert.False(result.Succeeded);
        Assert.Contains("already exists locally", result.Message);
        Assert.Single(history.Value!);
        Assert.Single(status.Value!.Files);
    }

    [Fact]
    public async Task PushTagConfirmedAsync_RequiresConfirmationAndPushesOnlyTheNamedTag()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var remote = sandbox.PathOf("release remote.git");
        await sandbox.RunOk(sandbox.Root, "init", "--bare", remote);
        await sandbox.RunOk(repo, "remote", "add", "origin", remote);
        await sandbox.RunOk(repo, "tag", "-a", "v0.6.0-beta", "-m", "Release v0.6.0-beta");
        var service = new ReleaseGitExecutionService(new GitService());

        var declined = await service.PushTagConfirmedAsync(repo, "origin", "v0.6.0-beta", confirmed: false);
        var absentBefore = await sandbox.RunAsync(repo, "ls-remote", "--tags", "--", "origin", "refs/tags/v0.6.0-beta");
        var pushed = await service.PushTagConfirmedAsync(repo, "origin", "v0.6.0-beta", confirmed: true);
        var presentAfter = await sandbox.RunAsync(repo, "ls-remote", "--tags", "--", "origin", "refs/tags/v0.6.0-beta");
        var repeated = await service.PushTagConfirmedAsync(repo, "origin", "v0.6.0-beta", confirmed: true);

        Assert.False(declined.Succeeded);
        Assert.Empty(absentBefore.StdOut);
        Assert.True(pushed.Succeeded, pushed.Error);
        Assert.Contains("refs/tags/v0.6.0-beta", presentAfter.StdOut);
        Assert.False(repeated.Succeeded);
        Assert.Contains("already exists on the remote", repeated.Error);
    }

    [Fact]
    public async Task PushTagConfirmedAsync_RefusesUnconfiguredRemoteAndMissingLocalTag()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var service = new ReleaseGitExecutionService(new GitService());

        var missingRemote = await service.PushTagConfirmedAsync(repo, "unknown", "v0.6.1-beta", true);
        await sandbox.RunOk(repo, "remote", "add", "origin", sandbox.PathOf("unused.git"));
        var missingTag = await service.PushTagConfirmedAsync(repo, "origin", "v0.6.1-beta", true);

        Assert.False(missingRemote.Succeeded);
        Assert.Contains("not configured", missingRemote.Error);
        Assert.False(missingTag.Succeeded);
        Assert.Contains("does not exist locally", missingTag.Error);
    }

    [Fact]
    public async Task PushTagConfirmedAsync_ReleasePlanRefusesRemoteChangedAfterPreview()
    {
        using var sandbox = new GitSandbox();
        var repo = await InitVersionRepo(sandbox);
        var project = Path.Combine(repo, "DevDesk.csproj");
        var preview = CreatePreview(project);
        var notesPath = ReleaseNotesSaveService.DefaultTargetPath(repo);
        Assert.True(ReleasePlanBuilder.TryCreate(
            repo,
            new GitHubRepositoryIdentity("github.com", "owner", "repo"),
            new GitRemoteInfo("origin", "https://github.com/owner/repo.git", "push"),
            preview,
            notesPath,
            "Notes",
            null,
            out var plan,
            out var planError), planError);

        var remote = sandbox.PathOf("changed remote.git");
        await sandbox.RunOk(sandbox.Root, "init", "--bare", remote);
        await sandbox.RunOk(repo, "remote", "add", "origin", remote);
        await sandbox.RunOk(repo, "tag", "-a", preview.Tag, "-m", "Release");

        var result = await new ReleaseGitExecutionService(new GitService())
            .PushTagConfirmedAsync(plan!, confirmed: true);
        var remoteTags = await sandbox.RunAsync(repo, "ls-remote", "--tags", "--", "origin", $"refs/tags/{preview.Tag}");

        Assert.False(result.Succeeded);
        Assert.Contains("remote changed after the release plan", result.Error);
        Assert.Empty(remoteTags.StdOut);
    }

    private static async Task<string> InitVersionRepo(GitSandbox sandbox)
    {
        var repo = await sandbox.InitRepoAsync("ReleaseRepo");
        await File.WriteAllTextAsync(
            Path.Combine(repo, "DevDesk.csproj"),
            ProjectXml("0.6.0", "0.6.0-beta"));
        await sandbox.RunOk(repo, "add", "--all");
        await sandbox.RunOk(repo, "commit", "-m", "baseline");
        return repo;
    }

    private static ReleaseVersionPreview CreatePreview(string projectFile)
    {
        var suggestion = new ReleaseVersionSuggestion(
            "0.6.0",
            "0.6.1",
            ReleaseBumpKind.Patch,
            ReleaseChannel.Beta,
            "v0.6.1-beta");
        Assert.True(ReleaseVersionPreviewBuilder.TryCreate(
            projectFile,
            suggestion,
            out var preview,
            out var error), error);
        return preview!;
    }

    private static string ProjectXml(string version, string informationalVersion) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        "  <PropertyGroup>\n" +
        $"    <Version>{version}</Version>\n" +
        $"    <AssemblyVersion>{version}.0</AssemblyVersion>\n" +
        $"    <FileVersion>{version}.0</FileVersion>\n" +
        $"    <InformationalVersion>{informationalVersion}</InformationalVersion>\n" +
        "  </PropertyGroup>\n" +
        "</Project>\n";
}
