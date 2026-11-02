using System;
using System.IO;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleasePlanBuilderTests
{
    private static readonly GitHubRepositoryIdentity Repository = new("github.com", "owner", "repo");

    [Fact]
    public void TryCreate_ProducesAnExactReadOnlyPlanWithRootReleaseNotesDefault()
    {
        using var sandbox = new PlanSandbox();
        var preview = sandbox.CreatePreview();
        var notesPath = ReleaseNotesSaveService.DefaultTargetPath(sandbox.Root);
        var originalProject = File.ReadAllText(sandbox.ProjectFile);

        var succeeded = ReleasePlanBuilder.TryCreate(
            sandbox.Root,
            Repository,
            new GitRemoteInfo("origin", "https://github.com/owner/repo.git", "push"),
            preview,
            notesPath,
            "## Features\n\n- A planned improvement\n",
            null,
            out var plan,
            out var error);

        Assert.True(succeeded, error);
        Assert.NotNull(plan);
        Assert.Equal(Path.GetFullPath(sandbox.Root), plan.RepositoryPath);
        Assert.Equal("origin", plan.RemoteName);
        Assert.Equal("Release v0.6.1-beta", plan.CommitMessage);
        Assert.Equal("v0.6.1-beta", plan.Tag);
        Assert.Equal("beta", plan.Channel);
        Assert.Equal(Path.GetFullPath(notesPath), plan.ReleaseNotesPath);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                new System.Text.UTF8Encoding(false).GetBytes(plan.ReleaseNotesMarkdown))),
            plan.ReleaseNotesSha256);
        Assert.Equal(originalProject, File.ReadAllText(sandbox.ProjectFile));
    }

    [Fact]
    public void TryCreate_RejectsRemoteOutsideSelectedRepository()
    {
        using var sandbox = new PlanSandbox();

        var succeeded = TryCreate(
            sandbox,
            new GitRemoteInfo("origin", "https://github.com/other/repo.git", "push"),
            sandbox.CreatePreview(),
            sandbox.DefaultNotesPath,
            "Notes",
            out var plan,
            out var error);

        Assert.False(succeeded);
        Assert.Null(plan);
        Assert.Contains("does not point to the selected GitHub repository", error);
    }

    [Fact]
    public void TryCreate_RequiresPushDirectionAndAnInRepositoryNotesPath()
    {
        using var sandbox = new PlanSandbox();
        var preview = sandbox.CreatePreview();

        var fetchOnly = TryCreate(
            sandbox,
            new GitRemoteInfo("origin", "https://github.com/owner/repo.git", "fetch"),
            preview,
            sandbox.DefaultNotesPath,
            "Notes",
            out var fetchPlan,
            out var fetchError);
        var outsidePath = TryCreate(
            sandbox,
            new GitRemoteInfo("origin", "https://github.com/owner/repo.git", "push"),
            preview,
            Path.Combine(Path.GetTempPath(), "external-notes.md"),
            "Notes",
            out var outsidePlan,
            out var outsideError);

        Assert.False(fetchOnly);
        Assert.Null(fetchPlan);
        Assert.Contains("push remote", fetchError);
        Assert.False(outsidePath);
        Assert.Null(outsidePlan);
        Assert.Contains("inside the selected repository", outsideError);
    }

    [Fact]
    public void TryCreate_RejectsStalePreviewAndInvalidCommitSubject()
    {
        using var sandbox = new PlanSandbox();
        var stalePreview = sandbox.CreatePreview();
        File.WriteAllText(sandbox.ProjectFile, PlanSandbox.ProjectXml("0.6.2", "0.6.2-beta"));
        var stale = TryCreate(
            sandbox,
            sandbox.Remote,
            stalePreview,
            sandbox.DefaultNotesPath,
            "Notes",
            out var stalePlan,
            out var staleError);
        File.WriteAllText(sandbox.ProjectFile, PlanSandbox.ProjectXml("0.6.0", "0.6.0-beta"));
        var invalidMessage = TryCreate(
            sandbox,
            sandbox.Remote,
            sandbox.CreatePreview(),
            sandbox.DefaultNotesPath,
            "Notes",
            "release\nwith second line",
            out var messagePlan,
            out var messageError);

        Assert.False(stale);
        Assert.Null(stalePlan);
        Assert.Contains("stale or invalid", staleError);
        Assert.False(invalidMessage);
        Assert.Null(messagePlan);
        Assert.Contains("single line", messageError);
    }

    private static bool TryCreate(
        PlanSandbox sandbox,
        GitRemoteInfo remote,
        ReleaseVersionPreview preview,
        string notesPath,
        string notes,
        out ReleasePlan? plan,
        out string error) =>
        TryCreate(sandbox, remote, preview, notesPath, notes, null, out plan, out error);

    private static bool TryCreate(
        PlanSandbox sandbox,
        GitRemoteInfo remote,
        ReleaseVersionPreview preview,
        string notesPath,
        string notes,
        string? commitMessage,
        out ReleasePlan? plan,
        out string error) =>
        ReleasePlanBuilder.TryCreate(
            sandbox.Root,
            Repository,
            remote,
            preview,
            notesPath,
            notes,
            commitMessage,
            out plan,
            out error);

    private sealed class PlanSandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "devdesk-release-plan-" + Guid.NewGuid().ToString("N"));
        public string ProjectFile => Path.Combine(Root, "DevDesk.csproj");
        public string DefaultNotesPath => ReleaseNotesSaveService.DefaultTargetPath(Root);
        public GitRemoteInfo Remote { get; } =
            new("origin", "https://github.com/owner/repo.git", "push");

        public PlanSandbox()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(ProjectFile, ProjectXml("0.6.0", "0.6.0-beta"));
        }

        public ReleaseVersionPreview CreatePreview()
        {
            var suggestion = new ReleaseVersionSuggestion(
                "0.6.0",
                "0.6.1",
                ReleaseBumpKind.Patch,
                ReleaseChannel.Beta,
                "v0.6.1-beta");
            Assert.True(ReleaseVersionPreviewBuilder.TryCreate(
                ProjectFile,
                suggestion,
                out var preview,
                out var error), error);
            return preview!;
        }

        public static string ProjectXml(string version, string informationalVersion) =>
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            $"    <Version>{version}</Version>\n" +
            $"    <AssemblyVersion>{version}.0</AssemblyVersion>\n" +
            $"    <FileVersion>{version}.0</FileVersion>\n" +
            $"    <InformationalVersion>{informationalVersion}</InformationalVersion>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n";

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
