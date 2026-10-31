using System;
using DevDesk.Services;

namespace DevDesk.Models;

public enum OperationKind
{
    Build,
    Run,
    Publish,
    Test,
}

public enum OperationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,

    /// <summary>Stopped by the user. Deliberately not a failure (roadmap §5.3).</summary>
    Canceled,
}

/// <summary>
/// One unit of work in the activity centre (W-0.3.6). Immutable: the centre
/// replaces the record when state changes, so a reader always holds a consistent
/// snapshot and the UI never renders half-updated state.
/// </summary>
public sealed record Operation(
    string Id,
    OperationKind Kind,
    string ProjectName,
    string ProjectPath,
    string Title,
    string Command,
    OperationStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    int? ExitCode = null,
    string? FirstError = null,
    string? LogPath = null,
    string? Hint = null,
    string Configuration = "",
    string Platform = "")
{
    public bool IsFinished => Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled;

    public bool IsRunning => Status == OperationStatus.Running;

    public TimeSpan? Duration =>
        StartedAt is { } started && FinishedAt is { } finished ? finished - started : null;

    public string DurationText => PerfCounters.FormatDuration(Duration);

    public string KindLabel => Kind switch
    {
        OperationKind.Build => "Build",
        OperationKind.Run => "Run",
        OperationKind.Publish => "Publish",
        OperationKind.Test => "Test",
        _ => "Operation",
    };

    public string StatusLabel => Status switch
    {
        OperationStatus.Queued => "Queued",
        OperationStatus.Running => "Running",
        OperationStatus.Succeeded => "Succeeded",
        OperationStatus.Failed => "Failed",
        OperationStatus.Canceled => "Cancelled",
        _ => Status.ToString(),
    };

    /// <summary>"12.4 s · exit 0" — a number that is absent prints "—", never a neighbour's value.</summary>
    public string ResultText => IsFinished
        ? $"{DurationText} · {(ExitCode is { } code ? $"exit {code}" : StatusLabel)}"
        : StatusLabel;
}

/// <summary>What a finished operation reports back to the centre.</summary>
public sealed record OperationOutcome(
    OperationStatus Status,
    int? ExitCode = null,
    string? FirstError = null,
    string? LogPath = null,
    string? Hint = null)
{
    public static OperationOutcome Succeeded(int exitCode, string? logPath = null) =>
        new(OperationStatus.Succeeded, exitCode, LogPath: logPath);

    public static OperationOutcome Failed(int? exitCode, string? firstError, string? logPath = null, string? hint = null) =>
        new(OperationStatus.Failed, exitCode, firstError, logPath, hint);

    public static OperationOutcome Canceled(string? logPath = null) =>
        new(OperationStatus.Canceled, LogPath: logPath, Hint: "Cancelled.");
}
