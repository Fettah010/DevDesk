using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// BuildService is the translation from a profile to a process, so every case
/// here checks the thing the user will actually experience: the argument list,
/// the log file, the first error, and the difference between a build that failed
/// and one they stopped.
/// </summary>
public class BuildServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Build_RunsTheBuildArgumentsAndWritesTheLog()
    {
        using var project = new TempProject();
        var solution = project.AddSolution();
        using var harness = new Harness(project.Root);

        var runner = harness.Runner;
        runner.Lines.Add(new ProcessOutputLine("Build succeeded.", false, DateTimeOffset.UnixEpoch));

        var profile = harness.Profile();
        var session = harness.Service.Build(harness.Project, profile);
        var finished = await session.Completion.WaitAsync(Wait);

        Assert.Equal(OperationStatus.Succeeded, finished.Status);
        Assert.Equal(OperationKind.Build, finished.Kind);
        Assert.Single(runner.Requests);
        Assert.Equal(
            new[] { "build", solution, "-c", "Debug", "-p:Platform=x64" },
            runner.Requests[0].Arguments);

        Assert.NotNull(finished.LogPath);
        Assert.True(File.Exists(finished.LogPath), "the log file was not written");
        var log = File.ReadAllText(finished.LogPath!);
        Assert.Contains("command: dotnet build", log, StringComparison.Ordinal);
        Assert.Contains("Build succeeded.", log, StringComparison.Ordinal);
        Assert.Contains("folder:", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_AFailureCarriesTheFirstErrorAndTheProblems()
    {
        using var project = new TempProject();
        project.AddSolution();
        using var harness = new Harness(project.Root);

        harness.Runner.Outcome = ProcessOutcome.Failed;
        harness.Runner.ExitCode = 1;
        harness.Runner.Lines.Add(new ProcessOutputLine(
            @"C:\Code\App.cs(1,1): error CS1002: ; expected [C:\Code\App.csproj]",
            true,
            DateTimeOffset.UnixEpoch));

        var session = harness.Service.Build(harness.Project, harness.Profile());
        var finished = await session.Completion.WaitAsync(Wait);

        Assert.Equal(OperationStatus.Failed, finished.Status);
        Assert.Equal(1, finished.ExitCode);
        Assert.Contains("CS1002", finished.FirstError, StringComparison.Ordinal);
        Assert.Single(session.Problems.Problems);
        Assert.Equal(1, session.Problems.ErrorCount);
    }

    [Fact]
    public async Task Build_ACancelledRunIsCanceledNotFailed()
    {
        using var project = new TempProject();
        project.AddSolution();
        using var harness = new Harness(project.Root);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runner.Before = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        };

        var session = harness.Service.Build(harness.Project, harness.Profile());
        await started.Task.WaitAsync(Wait);

        Assert.True(harness.Service.Activity.Cancel(session.OperationId));

        var finished = await session.Completion.WaitAsync(Wait);

        Assert.Equal(OperationStatus.Canceled, finished.Status);
        Assert.Equal(0, harness.Service.Activity.FailureCount);
    }

    [Fact]
    public async Task Run_BuildsFirstThenStartsTheBuiltExecutable()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("App");
        var exe = project.AddBuildOutput("App");
        using var harness = new Harness(project.Root, target: csproj);

        var session = harness.Service.Run(harness.Project, harness.Profile());
        await session.Completion.WaitAsync(Wait);

        Assert.Equal(2, harness.Runner.Requests.Count);
        Assert.Equal("build", harness.Runner.Requests[0].Arguments[0]);
        Assert.Equal(exe, harness.Runner.Requests[1].FileName);
        Assert.Empty(harness.Runner.Requests[1].Arguments);
        Assert.Equal(TimeSpan.Zero, harness.Runner.Requests[1].Timeout);
    }

    [Fact]
    public async Task Run_FallsBackToDotnetRunBeforeTheFirstBuild()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("App");
        using var harness = new Harness(project.Root, target: csproj);

        var session = harness.Service.Run(harness.Project, harness.Profile());
        await session.Completion.WaitAsync(Wait);

        Assert.Equal(2, harness.Runner.Requests.Count);
        Assert.Equal("run", harness.Runner.Requests[1].Arguments[0]);
        Assert.Equal(csproj, harness.Runner.Requests[1].Arguments[2]);
    }

    [Fact]
    public async Task Run_DoesNotStartAnythingWhenTheBuildFails()
    {
        using var project = new TempProject();
        var csproj = project.AddProject("App");
        project.AddBuildOutput("App");
        using var harness = new Harness(project.Root, target: csproj);

        harness.Runner.Outcome = ProcessOutcome.Failed;
        harness.Runner.ExitCode = 1;

        var session = harness.Service.Run(harness.Project, harness.Profile());
        var finished = await session.Completion.WaitAsync(Wait);

        Assert.Equal(OperationStatus.Failed, finished.Status);
        Assert.Single(harness.Runner.Requests);
    }

    [Fact]
    public async Task Publish_UsesPublishAndNeverChoosesAnOutputFolder()
    {
        using var project = new TempProject();
        project.AddSolution();
        using var harness = new Harness(project.Root);

        var session = harness.Service.Publish(harness.Project, harness.Profile());
        await session.Completion.WaitAsync(Wait);

        var arguments = harness.Runner.Requests[0].Arguments;
        Assert.Equal("publish", arguments[0]);
        Assert.DoesNotContain("-o", arguments);
        Assert.DoesNotContain("--output", arguments);
    }

    [Fact]
    public async Task Publish_ReportsTheFolderMSBuildWrote()
    {
        using var project = new TempProject();
        project.AddSolution();
        using var harness = new Harness(project.Root);

        harness.Runner.Lines.Add(new ProcessOutputLine(
            @"  App -> C:\out\publish\",
            false,
            DateTimeOffset.UnixEpoch));

        var session = harness.Service.Publish(harness.Project, harness.Profile());
        var finished = await session.Completion.WaitAsync(Wait);

        Assert.Equal(OperationStatus.Succeeded, finished.Status);
        Assert.Equal(@"C:\out\publish", finished.OutputPath);
        Assert.Equal(finished.OutputPath, session.OutputPath);
    }

    [Fact]
    public async Task Publish_AFailureOffersNoOutputFolder()
    {
        using var project = new TempProject();
        project.AddSolution();
        using var harness = new Harness(project.Root);

        harness.Runner.Outcome = ProcessOutcome.Failed;
        harness.Runner.ExitCode = 1;
        harness.Runner.Lines.Add(new ProcessOutputLine(
            @"  App -> C:\out\publish\",
            false,
            DateTimeOffset.UnixEpoch));

        var session = harness.Service.Publish(harness.Project, harness.Profile());
        var finished = await session.Completion.WaitAsync(Wait);

        Assert.Equal(OperationStatus.Failed, finished.Status);
        Assert.Null(finished.OutputPath);
        Assert.Null(session.OutputPath);
    }

    [Fact]
    public async Task TestSelf_UsesTheAppsOwnTestProject()
    {
        using var project = new TempProject();
        var tests = Path.Combine(project.Root, "DevDesk.Tests");
        Directory.CreateDirectory(tests);
        File.WriteAllText(
            Path.Combine(tests, "DevDesk.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using var harness = new Harness(project.Root);

        var session = harness.Service.TestSelf(harness.Project, new AppSettings());
        var finished = await session.Completion.WaitAsync(Wait);

        var arguments = harness.Runner.Requests[0].Arguments;
        Assert.Equal("test", arguments[0]);
        Assert.EndsWith("DevDesk.Tests.csproj", arguments[1], StringComparison.Ordinal);
        Assert.Contains("-p:Platform=x64", arguments);
        Assert.Equal(OperationKind.Test, finished.Kind);
    }

    [Fact]
    public void ResolveProfile_AFolderWithNoMarkerSaysWhy()
    {
        using var project = new TempProject();

        var resolution = BuildService.ResolveProfile(
            new ProjectModel { Name = "Empty", Path = project.Root, Branch = "main", Version = "1.0.0" },
            new AppSettings());

        Assert.False(resolution.IsBuildable);
        Assert.Contains("No .NET solution or project marker", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProfile_UsesThePerProjectOverride()
    {
        using var project = new TempProject();
        project.AddSolution();
        var settings = new AppSettings();
        settings.BuildProfiles[Path.GetFullPath(project.Root)] = new BuildProfileSettings
        {
            Configuration = "Release",
        };

        var resolution = BuildService.ResolveProfile(
            new ProjectModel { Name = "App", Path = project.Root, Branch = "main", Version = "1.0.0" },
            settings);

        Assert.Equal("Release", resolution.Profile!.Configuration);
    }

    private sealed class Harness : IDisposable
    {
        private readonly SettingsSandbox _sandbox = new();
        private readonly string _logDirectory;

        public Harness(string projectPath, string? target = null)
        {
            _logDirectory = Path.Combine(Path.GetTempPath(), "devdesk-logs-" + Guid.NewGuid().ToString("N"));
            Project = new ProjectModel
            {
                Name = Path.GetFileName(projectPath),
                Path = projectPath,
                Branch = "main",
                Version = "1.0.0",
            };

            var activity = new ActivityCenter(history: new BuildHistory(_sandbox.Directory));
            Service = new BuildService(
                Runner,
                activity,
                TimeProvider.System,
                new PerfCounters(),
                _logDirectory);

            Target = target;
        }

        public FakeProcessRunner Runner { get; } = new();

        public ProjectModel Project { get; }

        public string? Target { get; }

        public BuildService Service { get; }

        public BuildProfile Profile()
        {
            var resolution = BuildProfileResolver.Resolve(
                Project.Path,
                new BuildSettings(),
                Target is null ? null : new BuildProfileSettings { Target = Target });

            Assert.True(resolution.IsBuildable, resolution.Reason);
            return resolution.Profile!;
        }

        public void Dispose()
        {
            _sandbox.Dispose();

            try
            {
                if (Directory.Exists(_logDirectory))
                {
                    Directory.Delete(_logDirectory, true);
                }
            }
            catch (IOException)
            {
                // best-effort temp cleanup
            }
        }
    }
}
