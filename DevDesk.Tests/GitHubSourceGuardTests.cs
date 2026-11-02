using System;
using System.IO;
using Xunit;

namespace DevDesk.Tests;

public class GitHubSourceGuardTests
{
    [Fact]
    public void StartupAndLocalScanningDoNotIssueGitHubRequests()
    {
        var files = new[]
        {
            RepoLayout.Path("Program.cs"),
            RepoLayout.Path("App.xaml.cs"),
            RepoLayout.Path("Services", "ProjectCatalog.cs"),
            RepoLayout.Path("Services", "ScanEngine.cs")
        };
        var requestMarkers = new[]
        {
            "AppServices.GitHub",
            "GitHubApiClient",
            ".GetAuthenticatedUserAsync(",
            ".GetRepositoryAsync(",
            ".GetReleasesAsync(",
            ".GetWorkflowRunsAsync(",
            ".GetOpenPullRequestsAsync("
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var marker in requestMarkers)
            {
                Assert.False(
                    source.Contains(marker, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(RepoLayout.Root, file)} must not contain GitHub request marker '{marker}'.");
            }
        }
    }
}
