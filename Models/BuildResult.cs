using System;

namespace DevDesk.Models;

/// <summary>
/// The result of the smoke build that the template wizard runs on the new app.
/// Kept minimal: the UI only needs status, duration and where the log is.
/// </summary>
public sealed record BuildResult(
    string ProjectPath,
    string ProjectName,
    OperationStatus Status,
    TimeSpan Duration,
    int? ExitCode,
    string? LogPath,
    string? OutputPath)
{
    public bool Succeeded => Status == OperationStatus.Succeeded;

    /// <summary>"build succeeded · 4.5 s" or "build failed · exit 1".</summary>
    public string SummaryText
    {
        get
        {
            var verb = Status switch
            {
                OperationStatus.Succeeded => "succeeded",
                OperationStatus.Failed => "failed",
                OperationStatus.Canceled => "cancelled",
                _ => Status.ToString().ToLowerInvariant(),
            };
            var totalSeconds = Math.Max(0, Duration.TotalSeconds);
            var duration = totalSeconds >= 1
                ? $"{totalSeconds:0.0} s"
                : $"{totalSeconds * 1000:0} ms";
            return ExitCode is { } code
                ? $"build {verb} · {duration} · exit {code}"
                : $"build {verb} · {duration}";
        }
    }
}
