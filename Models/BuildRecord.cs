using System;
using DevDesk.Services;

namespace DevDesk.Models;

/// <summary>
/// One finished build/run/publish/test, as it is remembered (W-0.3.7).
/// <para>
/// This is the badge's only source: <see cref="ProjectModel"/> stays a pure scan
/// result, so "last build" can never appear inside a snapshot the scanner
/// produced (D-0.3.1).
/// </para>
/// </summary>
public sealed record BuildRecord(
    string ProjectPath,
    string ProjectName,
    OperationKind Kind,
    OperationStatus Status,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int? ExitCode,
    string? FirstError,
    string? LogPath,
    string Configuration,
    string Platform)
{
    public bool IsFailure => Status == OperationStatus.Failed;

    public string DurationText => PerfCounters.FormatDuration(Duration);

    /// <summary>"Built 12.4 s ago" is the Activity page's job; the badge wants a short fact.</summary>
    public string BadgeText
    {
        get
        {
            var verb = Kind switch
            {
                OperationKind.Build => "build",
                OperationKind.Run => "run",
                OperationKind.Publish => "publish",
                OperationKind.Test => "test",
                OperationKind.Release => "release",
                _ => "build",
            };

            return Status switch
            {
                OperationStatus.Succeeded => $"{verb} ok · {DurationText}",
                OperationStatus.Failed => $"{verb} failed",
                OperationStatus.Canceled => $"{verb} cancelled",
                _ => $"{verb} —",
            };
        }
    }
}
