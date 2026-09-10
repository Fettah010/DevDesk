using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ProjectOpenActionsTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void FindSolutionOrProject_PrefersRootSolution()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "Repo"));
        File.WriteAllText(Path.Combine(_tmp.Path, "Repo", "Proj.sln"), "sln");
        File.WriteAllText(Path.Combine(_tmp.Path, "Repo", "Proj.csproj"), "csproj");

        var found = ProjectOpenActions.FindSolutionOrProject(Path.Combine(_tmp.Path, "Repo"));

        Assert.Equal("Proj.sln", Path.GetFileName(found));
    }

    [Fact]
    public void FindSolutionOrProject_PrefersSlnxOverCsproj()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "Repo"));
        File.WriteAllText(Path.Combine(_tmp.Path, "Repo", "Proj.csproj"), "csproj");
        File.WriteAllText(Path.Combine(_tmp.Path, "Repo", "Proj.slnx"), "slnx");

        var found = ProjectOpenActions.FindSolutionOrProject(Path.Combine(_tmp.Path, "Repo"));

        Assert.Equal("Proj.slnx", Path.GetFileName(found));
    }

    [Fact]
    public void FindSolutionOrProject_FallsBackToNestedProject()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "Repo", "App"));
        File.WriteAllText(Path.Combine(_tmp.Path, "Repo", "App", "App.csproj"), "csproj");

        var found = ProjectOpenActions.FindSolutionOrProject(Path.Combine(_tmp.Path, "Repo"));

        Assert.NotNull(found);
        Assert.EndsWith("App.csproj", found, StringComparison.Ordinal);
    }

    [Fact]
    public void FindSolutionOrProject_SkipsBuildFolders()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "Repo", "bin"));
        File.WriteAllText(Path.Combine(_tmp.Path, "Repo", "bin", "skip.csproj"), "csproj");
        File.WriteAllText(Path.Combine(_tmp.Path, "Repo", "App.csproj"), "csproj");

        var found = ProjectOpenActions.FindSolutionOrProject(Path.Combine(_tmp.Path, "Repo"));

        Assert.EndsWith("App.csproj", found, StringComparison.Ordinal);
    }

    [Fact]
    public void FindSolutionOrProject_MissingRepo_ReturnsNull()
    {
        Assert.Null(ProjectOpenActions.FindSolutionOrProject(Path.Combine(_tmp.Path, "nope")));
    }

    [Fact]
    public void FindSolutionOrProject_EmptyRepo_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "Empty"));

        Assert.Null(ProjectOpenActions.FindSolutionOrProject(Path.Combine(_tmp.Path, "Empty")));
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devdesk-open-" + Guid.NewGuid().ToString("N"));

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