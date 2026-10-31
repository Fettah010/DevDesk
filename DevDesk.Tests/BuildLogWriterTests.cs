using System;
using System.Collections.Generic;
using System.IO;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The log file is the promise that "the console is bounded, the file is
/// complete". These cases cover the naming (findable by a human), the sanitizing
/// (a project name is not a path), and the pump (one line reaches the file, the
/// problems and the console from one call).
/// </summary>
public class BuildLogWriterTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 14, 5, 9, 123, TimeSpan.Zero);

    [Fact]
    public void Create_NamesTheFileWithTimeVerbAndProject()
    {
        using var sandbox = new SettingsSandbox();

        var path = BuildLogFile.Create(sandbox.Directory, OperationKind.Build, "Formixa.sln", At);

        Assert.Equal(
            Path.Combine(sandbox.Directory, "20261030-140509-123-build-Formixa.log"),
            path);
        Assert.True(Directory.Exists(sandbox.Directory));
    }

    [Fact]
    public void Create_UsesTheVerbForTheKind()
    {
        using var sandbox = new SettingsSandbox();

        var publish = BuildLogFile.Create(sandbox.Directory, OperationKind.Publish, "App.csproj", At);
        var test = BuildLogFile.Create(sandbox.Directory, OperationKind.Test, "Tests.csproj", At);

        Assert.EndsWith("-publish-App.log", publish, StringComparison.Ordinal);
        Assert.EndsWith("-test-Tests.log", test, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ReplacesCharactersAFileNameCannotHold()
    {
        using var sandbox = new SettingsSandbox();

        var path = BuildLogFile.Create(sandbox.Directory, OperationKind.Build, "My:App?.csproj", At);
        var name = Path.GetFileName(path);

        Assert.Equal("20261030-140509-123-build-My_App_.log", name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('?', name);
    }

    [Fact]
    public void Create_TruncatesALongProjectName()
    {
        using var sandbox = new SettingsSandbox();
        var longName = new string('a', 200) + ".csproj";

        var name = Path.GetFileName(BuildLogFile.Create(sandbox.Directory, OperationKind.Build, longName, At));

        Assert.EndsWith(".log", name, StringComparison.Ordinal);
        Assert.True(name.Length < 100, $"name was {name.Length} characters");
    }

    [Fact]
    public void Create_AFolderThatCannotNameAnythingStillGetsAFile()
    {
        using var sandbox = new SettingsSandbox();

        var name = Path.GetFileName(BuildLogFile.Create(sandbox.Directory, OperationKind.Build, ".csproj", At));

        Assert.EndsWith("-build-build.log", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_WritesEveryLineAndFlushes()
    {
        using var sandbox = new SettingsSandbox();
        var path = Path.Combine(sandbox.Directory, "one.log");

        using (var writer = new BuildLogWriter(path))
        {
            writer.WriteLine("first");
            writer.WriteLine("second");
        }

        Assert.Equal(new[] { "first", "second" }, File.ReadAllLines(path));
    }

    [Fact]
    public void Pump_SendsOneLineToTheFileTheProblemsAndTheConsole()
    {
        using var sandbox = new SettingsSandbox();
        var path = Path.Combine(sandbox.Directory, "pump.log");
        var console = new RecordingProgress();

        using var writer = new BuildLogWriter(path);
        var problems = new BuildProblemCollector();
        var pump = new BuildOutputPump(console, writer, problems, new TestClock());

        pump.Report(new ProcessOutputLine(@"A.cs(1,1): error CS1002: ; expected", false, At));
        pump.WriteLine("a line this app wrote");

        // Close before reading: the log is meant to be opened by a human while
        // the next run is not writing to it.
        writer.Dispose();

        var text = File.ReadAllText(path);
        Assert.Contains("CS1002", text, StringComparison.Ordinal);
        Assert.Contains("a line this app wrote", text, StringComparison.Ordinal);
        Assert.Single(problems.Problems);
        Assert.Equal(2, console.Lines.Count);
    }

    [Fact]
    public void Pump_KeepsTheLastOutputPathMSBuildReported()
    {
        using var sandbox = new SettingsSandbox();
        using var writer = new BuildLogWriter(Path.Combine(sandbox.Directory, "paths.log"));
        var pump = new BuildOutputPump(null, writer, new BuildProblemCollector(), new TestClock());

        pump.Report(new ProcessOutputLine(@"  App -> C:\out\bin\Debug\net10.0\App.dll", false, At));
        pump.Report(new ProcessOutputLine(@"  App -> C:\out\bin\Debug\net10.0\publish\", false, At));

        // dotnet publish prints the assembly first and the folder last; the
        // folder is the one a user would open (D-0.3.3).
        Assert.Equal(@"C:\out\bin\Debug\net10.0\publish\", pump.OutputPath);
    }

    [Fact]
    public void Pump_ALineWithoutAnArrowLeavesThePathAlone()
    {
        using var sandbox = new SettingsSandbox();
        using var writer = new BuildLogWriter(Path.Combine(sandbox.Directory, "paths.log"));
        var pump = new BuildOutputPump(null, writer, new BuildProblemCollector(), new TestClock());

        pump.Report(new ProcessOutputLine("Build succeeded.", false, At));
        Assert.Null(pump.OutputPath);

        pump.Report(new ProcessOutputLine(@"  App -> C:\out\publish\", false, At));
        pump.Report(new ProcessOutputLine("Time Elapsed 00:00:02.11", false, At));

        Assert.Equal(@"C:\out\publish\", pump.OutputPath);
    }

    [Fact]
    public void Writer_APathThatCannotBeOpenedIsNotAnError()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "devdesk-missing-" + Guid.NewGuid().ToString("N"),
            "nested",
            "x.log");

        using var writer = new BuildLogWriter(missing);
        writer.WriteLine("ignored");

        Assert.False(writer.IsOpen);
    }

    private sealed class RecordingProgress : IProgress<ProcessOutputLine>
    {
        public List<ProcessOutputLine> Lines { get; } = new();

        public void Report(ProcessOutputLine value) => Lines.Add(value);
    }
}
