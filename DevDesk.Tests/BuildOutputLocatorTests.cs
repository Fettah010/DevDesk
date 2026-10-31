using System.IO;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// "Run" must never launch something the user did not point at. The rules are
/// about ambiguity: one application project or a stated reason, never a guess
/// between several.
/// </summary>
public class BuildOutputLocatorTests
{
    private static BuildProfile Profile(string target) =>
        new(Path.GetDirectoryName(target)!, target, Path.GetFileName(target), "Debug", "x64", true, false, System.Array.Empty<string>());

    [Fact]
    public void ResolveRunTarget_ASingleApplicationProjectIsRunnable()
    {
        using var project = new TempProject();
        var solution = project.AddSolution();
        project.AddProject("Formixa", outputType: "WinExe");

        var run = BuildOutputLocator.ResolveRunTarget(Profile(solution));

        Assert.True(run.CanRun);
        Assert.Equal("Formixa", run.AssemblyName);
        Assert.Null(run.Reason);
    }

    [Fact]
    public void ResolveRunTarget_AProjectTargetIsUsedDirectly()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("Solo", outputType: "Exe", assemblyName: "SoloApp");

        var run = BuildOutputLocator.ResolveRunTarget(Profile(csproj));

        Assert.True(run.CanRun);
        Assert.Equal("SoloApp", run.AssemblyName);
    }

    [Fact]
    public void ResolveRunTarget_ASolutionWithoutAProjectSaysWhy()
    {
        using var project = new TempProject();
        var solution = project.AddSolutionX();

        var run = BuildOutputLocator.ResolveRunTarget(Profile(solution));

        Assert.False(run.CanRun);
        Assert.Contains("No application project at the repository root", run.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRunTarget_ASolutionWithSeveralProjectsRefusesToGuess()
    {
        using var project = new TempProject();
        var solution = project.AddSolution();
        project.AddProject("One");
        project.AddProject("Two");

        var run = BuildOutputLocator.ResolveRunTarget(Profile(solution));

        Assert.False(run.CanRun);
        Assert.Contains("2 projects", run.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRunTarget_ALibraryIsNotAnApplication()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("Lib", outputType: "Library");

        var run = BuildOutputLocator.ResolveRunTarget(Profile(csproj));

        Assert.False(run.CanRun);
        Assert.Contains("library", run.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindExecutable_FindsThePlatformOutputOfTheLastBuild()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("App");
        var exe = project.AddBuildOutput("App");

        var profile = Profile(csproj);
        var run = BuildOutputLocator.ResolveRunTarget(profile);

        Assert.Equal(exe, BuildOutputLocator.FindExecutable(profile, run));
    }

    [Fact]
    public void FindExecutable_FallsBackToTheConfigurationOnlyOutput()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("App");
        var folder = Path.Combine(project.Root, "bin", "Debug", "net10.0-windows10.0.19041.0");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "App.exe");
        File.WriteAllText(exe, "not a real executable");

        var profile = Profile(csproj);
        var run = BuildOutputLocator.ResolveRunTarget(profile);

        Assert.Equal(exe, BuildOutputLocator.FindExecutable(profile, run));
    }

    [Fact]
    public void FindExecutable_ReturnsNullBeforeTheFirstBuild()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("App");

        var profile = Profile(csproj);
        var run = BuildOutputLocator.ResolveRunTarget(profile);

        Assert.Null(BuildOutputLocator.FindExecutable(profile, run));
    }

    [Fact]
    public void FindExecutable_ReturnsNullWhenTheRunTargetIsNotRunnable()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("Lib", outputType: "Library");

        var profile = Profile(csproj);
        var run = BuildOutputLocator.ResolveRunTarget(profile);

        Assert.Null(BuildOutputLocator.FindExecutable(profile, run));
    }
}
