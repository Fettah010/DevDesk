using System;
using System.IO;
using System.Linq;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The history file is the only source of a build badge, so the failures that
/// matter are the quiet ones: a ring that grows without bound, a corrupt file
/// that stops the app, and a record that loses its project on the way back in.
/// </summary>
public class BuildHistoryTests
{
    private static BuildRecord Record(
        string path = @"C:\Code\DevEcosystem\Formixa",
        OperationKind kind = OperationKind.Build,
        OperationStatus status = OperationStatus.Succeeded,
        string? error = null,
        int? exitCode = 0) =>
        new(
            path,
            Path.GetFileName(path),
            kind,
            status,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1.5),
            exitCode,
            error,
            @"C:\logs\build.log",
            "Debug",
            "x64");

    [Fact]
    public void Add_NewestFirstAndLastForFindsTheProject()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);

        history.Add(Record());
        history.Add(Record(status: OperationStatus.Failed, error: "error CS1002"));

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal(OperationStatus.Failed, history.Entries[0].Status);
        Assert.Equal(OperationStatus.Failed, history.LastFor(@"C:\Code\DevEcosystem\Formixa")!.Status);
    }

    [Fact]
    public void Add_CapsTheRingAtTheLimit()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);

        for (var index = 0; index < 5; index++)
        {
            history.Add(Record(path: $@"C:\Code\DevEcosystem\Project{index}"), limit: 3);
        }

        Assert.Equal(3, history.Entries.Count);
        Assert.Equal("Project4", history.Entries[0].ProjectName);
        Assert.Equal("Project2", history.Entries[2].ProjectName);
    }

    [Fact]
    public void Add_AnEntryWithNoPathIsNotStoredOnReload()
    {
        using var sandbox = new SettingsSandbox();
        File.WriteAllText(
            Path.Combine(sandbox.Directory, "build-history.json"),
            """
            {
              "schemaVersion": 1,
              "writtenAt": "2026-01-01T00:00:00+00:00",
              "entries": [
                { "projectPath": "", "projectName": "ghost", "kind": "Build", "status": "Succeeded" },
                { "projectPath": "C:\\Code\\DevEcosystem\\Formixa", "projectName": "Formixa", "kind": "Build", "status": "Succeeded" }
              ]
            }
            """);

        var history = new BuildHistory(sandbox.Directory);

        Assert.Single(history.Entries);
        Assert.Equal("Formixa", history.Entries[0].ProjectName);
    }

    [Fact]
    public void Entries_RoundTripThroughASecondInstance()
    {
        using var sandbox = new SettingsSandbox();
        var clock = new TestClock();
        var first = new BuildHistory(sandbox.Directory, clock);

        first.Add(Record(status: OperationStatus.Canceled, exitCode: null));

        var second = new BuildHistory(sandbox.Directory, clock);
        var record = second.LastFor(@"C:\Code\DevEcosystem\Formixa");

        Assert.NotNull(record);
        Assert.Equal(OperationStatus.Canceled, record!.Status);
        Assert.Equal(TimeSpan.FromSeconds(1.5), record.Duration);
        Assert.Null(record.ExitCode);
        Assert.Equal("Debug", record.Configuration);
        Assert.Equal("x64", record.Platform);
    }

    [Fact]
    public void LastFor_IsCaseAndSeparatorInsensitive()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);

        history.Add(Record(path: @"C:\Code\DevEcosystem\Formixa"));

        Assert.NotNull(history.LastFor(@"c:\code\devecosystem\formixa"));
        Assert.NotNull(history.LastFor(@"C:\Code\DevEcosystem\Formixa\"));
    }

    [Fact]
    public void LastFor_ReturnsNullForAProjectThatNeverBuilt()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);

        Assert.Null(history.LastFor(@"C:\Code\DevEcosystem\Never"));
        Assert.Null(history.LastFor(null));
        Assert.Null(history.LastFor("  "));
    }

    [Fact]
    public void Load_ACorruptFileStartsEmptyAndKeepsABackup()
    {
        using var sandbox = new SettingsSandbox();
        File.WriteAllText(Path.Combine(sandbox.Directory, "build-history.json"), "{ this is not json");

        var history = new BuildHistory(sandbox.Directory);

        Assert.Empty(history.Entries);
        Assert.NotEmpty(Directory.GetFiles(sandbox.Directory, "build-history.corrupt-*.json"));
    }

    [Fact]
    public void Load_AFileFromANewerSchemaIsDiscardedNotGuessed()
    {
        using var sandbox = new SettingsSandbox();
        File.WriteAllText(
            Path.Combine(sandbox.Directory, "build-history.json"),
            """
            { "schemaVersion": 99, "entries": [ { "projectPath": "C:\\x", "projectName": "x" } ] }
            """);

        var history = new BuildHistory(sandbox.Directory);

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Clear_EmptiesTheFileToo()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        history.Add(Record());

        history.Clear();

        Assert.Empty(history.Entries);
        Assert.Empty(new BuildHistory(sandbox.Directory).Entries);
    }

    [Fact]
    public void BadgeText_SaysWhatHappened()
    {
        Assert.Equal("build ok · 1.50 s", Record().BadgeText);
        Assert.Equal("build failed", Record(status: OperationStatus.Failed).BadgeText);
        Assert.Equal("build cancelled", Record(status: OperationStatus.Canceled).BadgeText);
        Assert.Equal("publish ok · 1.50 s", Record(kind: OperationKind.Publish).BadgeText);
    }

    [Fact]
    public void HistoryPath_IsUnderTheInjectedDirectory()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(sandbox.Directory), "build-history.json"),
            history.HistoryPath);
    }
}
