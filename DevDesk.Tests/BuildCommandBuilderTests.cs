using System;
using System.Linq;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The exact argument list is the safety property: it is what keeps a path with
/// spaces one argument, keeps <c>--no-restore</c> in the right place, and keeps a
/// publish from writing into a folder the user did not choose.
/// </summary>
public class BuildCommandBuilderTests
{
    private static BuildProfile Profile(
        string target = @"C:\Code\DevEcosystem\Formixa\Formixa.sln",
        string configuration = "Debug",
        string platform = "x64",
        bool platformApplies = true,
        bool noRestore = false,
        params string[] extra) =>
        new(
            @"C:\Code\DevEcosystem\Formixa",
            target,
            System.IO.Path.GetFileName(target),
            configuration,
            platform,
            platformApplies,
            noRestore,
            extra);

    [Fact]
    public void Build_IsTheExpectedArgumentList()
    {
        var arguments = BuildCommandBuilder.BuildArguments(Profile());

        Assert.Equal(
            new[] { "build", @"C:\Code\DevEcosystem\Formixa\Formixa.sln", "-c", "Debug", "-p:Platform=x64" },
            arguments);
    }

    [Fact]
    public void Build_OmitsThePlatformWhenTheProjectDoesNotUseIt()
    {
        var arguments = BuildCommandBuilder.BuildArguments(Profile(platformApplies: false));

        Assert.DoesNotContain(arguments, argument => argument.StartsWith("-p:Platform", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AddsNoRestoreAndExtraArgumentsInOrder()
    {
        var arguments = BuildCommandBuilder.BuildArguments(
            Profile(noRestore: true, extra: new[] { "/p:RunAnalyzers=false" }));

        Assert.Equal("--no-restore", arguments[5]);
        Assert.Equal("/p:RunAnalyzers=false", arguments[6]);
    }

    [Fact]
    public void Publish_NeverChoosesAnOutputFolder()
    {
        var arguments = BuildCommandBuilder.PublishArguments(Profile(configuration: "Release"));

        Assert.Equal("publish", arguments[0]);
        Assert.DoesNotContain("-o", arguments);
        Assert.DoesNotContain("--output", arguments);
    }

    [Fact]
    public void Test_UsesTheSameShapeAsBuild()
    {
        var arguments = BuildCommandBuilder.TestArguments(Profile());

        Assert.Equal("test", arguments[0]);
        Assert.Contains("-p:Platform=x64", arguments);
    }

    [Fact]
    public void Run_FallsBackToDotnetRunWithoutNoBuild()
    {
        var arguments = BuildCommandBuilder.RunArguments(Profile());

        Assert.Equal("run", arguments[0]);
        Assert.Equal("--project", arguments[1]);
        Assert.DoesNotContain("--no-build", arguments);
    }

    [Fact]
    public void Requests_CarryTheProjectFolderAndTheTimeoutPolicy()
    {
        var build = BuildCommandBuilder.BuildRequest(Profile());
        var run = BuildCommandBuilder.RunRequest(Profile());

        Assert.Equal("dotnet", build.FileName);
        Assert.Equal(@"C:\Code\DevEcosystem\Formixa", build.WorkingDirectory);
        Assert.Null(build.Timeout);

        // A launched application has no timeout: the user stops it.
        Assert.Equal(TimeSpan.Zero, run.Timeout);
    }

    [Fact]
    public void Preview_QuotesAPathWithSpacesForReading()
    {
        var preview = BuildCommandBuilder.Preview(
            BuildCommandBuilder.BuildArguments(Profile(target: @"C:\Code\DevEcosystem\DevTem WinUI 3\DevTemWinUi3.sln")));

        Assert.StartsWith("dotnet build \"C:\\Code\\DevEcosystem\\DevTem WinUI 3\\DevTemWinUi3.sln\"", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_IsOnlyBuiltForDisplayAndNeverExecuted()
    {
        // The request keeps a list: the preview string must not be what runs.
        var request = BuildCommandBuilder.BuildRequest(Profile());

        Assert.Equal(5, request.Arguments.Count);
        Assert.All(request.Arguments, argument => Assert.DoesNotContain("dotnet build", argument, StringComparison.Ordinal));
    }

    [Fact]
    public void Build_KeepsTheVerbAndTargetFirst()
    {
        var arguments = BuildCommandBuilder.BuildArguments(Profile()).ToArray();

        Assert.Equal("build", arguments[0]);
        Assert.EndsWith(".sln", arguments[1], StringComparison.Ordinal);
    }
}
