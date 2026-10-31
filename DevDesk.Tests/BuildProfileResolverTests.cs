using System;
using System.IO;
using System.Linq;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// A profile is the answer to "what exactly will this button run?". Every rule is
/// checked here because the failure mode is silent: the wrong platform or a
/// dropped argument produces a build that fails for a reason nobody can see.
/// </summary>
public class BuildProfileResolverTests
{
    [Fact]
    public void Resolve_UsesTheSettingsDefaults()
    {
        using var project = new TempProject();
        var solution = project.AddSolution();

        var resolution = BuildProfileResolver.Resolve(project.Root, new BuildSettings());

        Assert.True(resolution.IsBuildable);
        Assert.Equal("Debug", resolution.Profile!.Configuration);
        Assert.Equal("x64", resolution.Profile.Platform);
        Assert.Equal(solution, resolution.Profile.TargetPath);
        Assert.Equal("App.sln", resolution.Profile.TargetName);
        Assert.Null(resolution.Reason);
    }

    [Fact]
    public void Resolve_APerProjectOverrideWins()
    {
        using var project = new TempProject();
        project.AddSolution();

        var overrides = new BuildProfileSettings
        {
            Configuration = "Release",
            Platform = "ARM64",
            NoRestore = true,
            ExtraArgs = "/p:RunAnalyzers=false",
        };

        var resolution = BuildProfileResolver.Resolve(project.Root, new BuildSettings(), overrides);

        Assert.Equal("Release", resolution.Profile!.Configuration);
        Assert.Equal("ARM64", resolution.Profile.Platform);
        Assert.True(resolution.Profile.NoRestore);
        Assert.Equal(new[] { "/p:RunAnalyzers=false" }, resolution.Profile.ExtraArguments);
    }

    [Fact]
    public void Resolve_AFolderWithNoMarkerSaysWhyInsteadOfDisablingSilently()
    {
        using var project = new TempProject();

        var resolution = BuildProfileResolver.Resolve(project.Root, new BuildSettings());

        Assert.False(resolution.IsBuildable);
        Assert.Null(resolution.Profile);
        Assert.Contains("No .NET solution or project marker", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AMissingFolderIsNotBuildable()
    {
        var resolution = BuildProfileResolver.Resolve(
            Path.Combine(Path.GetTempPath(), "devdesk-nope-" + Guid.NewGuid().ToString("N")),
            new BuildSettings());

        Assert.False(resolution.IsBuildable);
        Assert.Contains("does not exist", resolution.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_ASolutionAlwaysPassesThePlatform()
    {
        using var project = new TempProject();
        var solution = project.AddSolution();

        var resolution = BuildProfileResolver.Resolve(project.Root, new BuildSettings());

        Assert.True(resolution.Profile!.PlatformApplies);
        Assert.Equal(solution, resolution.Profile.TargetPath);
        Assert.Equal("Debug · x64", resolution.Profile.Summary);
    }

    [Fact]
    public void Resolve_APlainProjectDoesNotInventAPlatform()
    {
        using var project = new TempProject();
        project.AddProject("Console", outputType: "Exe", windowsAppSdk: false, targetFramework: "net10.0");

        var resolution = BuildProfileResolver.Resolve(project.Root, new BuildSettings());

        Assert.False(resolution.Profile!.PlatformApplies);
        Assert.Equal("Debug", resolution.Profile.Summary);
    }

    [Fact]
    public void Resolve_AWinUiProjectKeepsThePlatformEvenWithoutAPlatformsList()
    {
        using var project = new TempProject();
        project.AddProject("App", windowsAppSdk: true, platforms: null);

        var resolution = BuildProfileResolver.Resolve(project.Root, new BuildSettings());

        Assert.True(resolution.Profile!.PlatformApplies);
    }

    [Fact]
    public void Resolve_AProjectThatEnumeratesPlatformsDecides()
    {
        using var project = new TempProject();
        project.AddProject("Tool", outputType: "Exe", windowsAppSdk: false, platforms: "x86;ARM64");

        var x64 = BuildProfileResolver.Resolve(project.Root, new BuildSettings { Platform = "x64" });
        var arm = BuildProfileResolver.Resolve(project.Root, new BuildSettings { Platform = "ARM64" });

        Assert.False(x64.Profile!.PlatformApplies);
        Assert.True(arm.Profile!.PlatformApplies);
    }

    [Fact]
    public void Resolve_AMissingSavedTargetFallsBackAndWarns()
    {
        using var project = new TempProject();
        var solution = project.AddSolution();

        var resolution = BuildProfileResolver.Resolve(
            project.Root,
            new BuildSettings(),
            new BuildProfileSettings { Target = "gone.csproj" });

        Assert.Equal(solution, resolution.Profile!.TargetPath);
        Assert.NotNull(resolution.Warning);
        Assert.Contains("gone.csproj", resolution.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnExplicitTargetIsUsed()
    {
        using var project = new TempProject();
        project.AddSolution();
        var wanted = project.AddProject("Only");

        var resolution = BuildProfileResolver.Resolve(
            project.Root,
            new BuildSettings(),
            new BuildProfileSettings { Target = "Only.csproj" });

        Assert.Equal(wanted, resolution.Profile!.TargetPath);
    }

    [Fact]
    public void SplitArguments_HonoursQuotesAndSpaces()
    {
        var arguments = BuildProfileResolver.SplitArguments("  /p:A=1  \"-p:B=two words\"  ");

        Assert.Equal(new[] { "/p:A=1", "-p:B=two words" }, arguments);
    }

    [Fact]
    public void SplitArguments_EmptyInputIsAnEmptyList()
    {
        Assert.Empty(BuildProfileResolver.SplitArguments(null));
        Assert.Empty(BuildProfileResolver.SplitArguments("   "));
    }

    [Theory]
    [InlineData("arm64", "ARM64")]
    [InlineData("X86", "x86")]
    [InlineData("nonsense", "x64")]
    [InlineData("", "x64")]
    [InlineData(null, "x64")]
    public void NormalizePlatform_KeepsTheKnownVocabulary(string? input, string expected)
    {
        Assert.Equal(expected, BuildProfileResolver.NormalizePlatform(input));
    }

    [Fact]
    public void Resolve_AWhitespaceConfigurationFallsBackToDebug()
    {
        using var project = new TempProject();
        project.AddSolution();

        var resolution = BuildProfileResolver.Resolve(
            project.Root,
            new BuildSettings { Configuration = "   " });

        Assert.Equal("Debug", resolution.Profile!.Configuration);
    }

    [Fact]
    public void Resolve_DoesNotTreatAMarkerlessFolderAsBuildable()
    {
        using var project = new TempProject();
        File.WriteAllText(Path.Combine(project.Root, "README.md"), "no project here");

        var resolution = BuildProfileResolver.Resolve(project.Root, new BuildSettings());

        Assert.False(resolution.IsBuildable);
        Assert.DoesNotContain("README", resolution.Reason!, StringComparison.Ordinal);
    }
}
