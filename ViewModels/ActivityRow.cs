using System;
using DevDesk.Models;
using Microsoft.UI.Xaml;

namespace DevDesk.ViewModels;

/// <summary>
/// One line on the Activity page, whether it happened in this session or was
/// read back from <c>build-history.json</c> (W-0.3.6, D-0.3.8).
/// <para>
/// Two sources, one row shape: the page must not look different after a restart,
/// and a stored row has no session id, so it can be opened but not retried. The
/// absence of Retry on an old row is a fact about the request no longer existing,
/// not a disabled button apologising.
/// </para>
/// </summary>
public sealed record ActivityRow(
    string Id,
    string Title,
    string ProjectName,
    string ProjectPath,
    OperationKind Kind,
    OperationStatus Status,
    string ResultText,
    string Detail,
    DateTimeOffset At,
    string? FirstError,
    string? LogPath,
    bool IsSession)
{
    /// <summary>The row for an operation this session still owns.</summary>
    public static ActivityRow FromOperation(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return new ActivityRow(
            operation.Id,
            operation.Title,
            operation.ProjectName,
            operation.ProjectPath,
            operation.Kind,
            operation.Status,
            ResultTextFor(operation.Status, operation.Duration, operation.ExitCode),
            DetailFor(operation.Configuration, operation.Platform),
            operation.StartedAt ?? operation.QueuedAt,
            operation.FirstError,
            operation.LogPath,
            IsSession: true);
    }

    /// <summary>A finished run read back from disk. It has no request, so no Retry.</summary>
    public static ActivityRow FromRecord(BuildRecord record, int index)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ActivityRow(
            $"history-{index}",
            $"{LabelFor(record.Kind)} {record.ProjectName}",
            record.ProjectName,
            record.ProjectPath,
            record.Kind,
            record.Status,
            ResultTextFor(record.Status, record.Duration, record.ExitCode),
            DetailFor(record.Configuration, record.Platform),
            record.StartedAt,
            record.FirstError,
            record.LogPath,
            IsSession: false);
    }

    public string WhenText => At.ToLocalTime().ToString("HH:mm:ss");

    public string KindLabel => LabelFor(Kind);

    public string StatusLabel => Status switch
    {
        OperationStatus.Queued => "Queued",
        OperationStatus.Running => "Running",
        OperationStatus.Succeeded => "Succeeded",
        OperationStatus.Failed => "Failed",
        OperationStatus.Canceled => "Cancelled",
        _ => Status.ToString(),
    };

    public string Glyph => Status switch
    {
        OperationStatus.Succeeded => "\uE73E",
        OperationStatus.Failed => "\uE783",
        OperationStatus.Canceled => "\uE71A",
        OperationStatus.Running => "\uE895",
        _ => "\uE823",
    };

    public bool CanStop => IsSession && Status is OperationStatus.Queued or OperationStatus.Running;

    public bool CanRetry => IsSession
        && Kind != OperationKind.Release
        && Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled;

    public bool HasLog => !string.IsNullOrWhiteSpace(LogPath);

    public string DetailText => string.IsNullOrWhiteSpace(Detail)
        ? $"{KindLabel} · {WhenText}"
        : $"{KindLabel} · {Detail} · {WhenText}";

    public Visibility StopVisibility => Visible(CanStop);

    public Visibility RetryVisibility => Visible(CanRetry);

    public Visibility LogVisibility => Visible(HasLog);

    public Visibility ErrorVisibility => Visible(!string.IsNullOrWhiteSpace(FirstError));

    public Visibility QueuedVisibility => Visible(Status == OperationStatus.Queued);

    public Visibility RunningVisibility => Visible(Status == OperationStatus.Running);

    public Visibility SucceededVisibility => Visible(Status == OperationStatus.Succeeded);

    public Visibility FailedVisibility => Visible(Status == OperationStatus.Failed);

    public Visibility CanceledVisibility => Visible(Status == OperationStatus.Canceled);

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private static string LabelFor(OperationKind kind) => kind switch
    {
        OperationKind.Build => "Build",
        OperationKind.Run => "Run",
        OperationKind.Publish => "Publish",
        OperationKind.Test => "Test",
        OperationKind.Release => "Release",
        _ => "Operation",
    };

    private static string DetailFor(string configuration, string platform)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return platform;
        }

        return string.IsNullOrWhiteSpace(platform) ? configuration : $"{configuration} · {platform}";
    }

    /// <summary>
    /// "12.4 s · exit 0", "1.2 s · stopped", "—" while it has no duration. A
    /// missing number is a dash, never the neighbouring row's value (gotcha 16).
    /// </summary>
    private static string ResultTextFor(OperationStatus status, TimeSpan? duration, int? exitCode)
    {
        var time = duration is { } elapsed ? Services.PerfCounters.FormatDuration(elapsed) : "—";

        return status switch
        {
            OperationStatus.Succeeded => $"{time} · exit {exitCode ?? 0}",
            OperationStatus.Failed => exitCode is { } code ? $"{time} · exit {code}" : time,
            OperationStatus.Canceled => $"{time} · stopped",
            _ => "—",
        };
    }
}
