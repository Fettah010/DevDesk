using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

public interface IReleaseWorkflowRunReader
{
    Task<GitHubDataResult<GitHubWorkflowRuns>> GetWorkflowRunsAsync(
        GitHubRepositoryIdentity identity,
        bool forceRefresh,
        CancellationToken cancellationToken = default);
}

public enum ReleaseWorkflowMonitorState
{
    AwaitingRun,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    MonitoringStopped,
    Error
}

public sealed record ReleaseWorkflowMonitorUpdate(
    ReleaseWorkflowMonitorState State,
    GitHubWorkflowRun? Run,
    string? RunUrl,
    TimeSpan Elapsed,
    string? Message);

public sealed record ReleaseWorkflowMonitorOptions(
    TimeSpan MaximumDuration,
    TimeSpan[] PollIntervals)
{
    public static ReleaseWorkflowMonitorOptions Default { get; } = new(
        TimeSpan.FromMinutes(10),
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)]);
}

public sealed class ReleaseWorkflowMonitor
{
    public const string WorkflowPath = ".github/workflows/release.yml";

    private readonly IReleaseWorkflowRunReader _reader;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ReleaseWorkflowMonitorOptions _options;

    public ReleaseWorkflowMonitor(
        IReleaseWorkflowRunReader reader,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ReleaseWorkflowMonitorOptions? options = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _time = timeProvider ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
        _options = options ?? ReleaseWorkflowMonitorOptions.Default;
        ArgumentNullException.ThrowIfNull(_options.PollIntervals);
        if (_options.MaximumDuration <= TimeSpan.Zero
            || _options.PollIntervals.Length == 0
            || _options.PollIntervals.Any(interval => interval <= TimeSpan.Zero))
        {
            throw new ArgumentException("Workflow polling must have positive intervals and a positive time limit.", nameof(options));
        }
    }

    public async Task<ReleaseWorkflowMonitorUpdate> MonitorAsync(
        GitHubRepositoryIdentity identity,
        string tag,
        string headSha,
        IProgress<ReleaseWorkflowMonitorUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        if (!identity.SupportsPublicApi)
        {
            throw new ArgumentException("Release workflow monitoring is supported only for github.com repositories.", nameof(identity));
        }

        if (!IsRepositorySegment(identity.Owner) || !IsRepositorySegment(identity.Repository))
        {
            throw new ArgumentException("The selected GitHub repository identity is invalid.", nameof(identity));
        }

        if (!ReleaseChannels.TryParseTagTyped(tag, out var version, out var channel)
            || !string.Equals(
                tag,
                channel == ReleaseChannel.Stable
                    ? $"v{version}"
                    : $"v{version}-{ReleaseChannels.ToName(channel)}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical stable, beta, or dev release tag is required.", nameof(tag));
        }

        if (headSha.Length is not (40 or 64) || !headSha.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The pushed commit SHA must be a 40- or 64-character hexadecimal value.", nameof(headSha));
        }

        var started = _time.GetTimestamp();
        var pollIndex = 0;
        GitHubWorkflowRun? lastRun = null;
        string? lastRunUrl = null;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Report(ReleaseWorkflowMonitorState.MonitoringStopped, lastRun, lastRunUrl, null);
            }

            var elapsed = _time.GetElapsedTime(started);
            if (elapsed >= _options.MaximumDuration)
            {
                return Report(ReleaseWorkflowMonitorState.TimedOut, lastRun, lastRunUrl,
                    "Monitoring reached its time limit; the release workflow may still be running.");
            }

            GitHubDataResult<GitHubWorkflowRuns> response;
            try
            {
                response = await _reader.GetWorkflowRunsAsync(identity, forceRefresh: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Report(ReleaseWorkflowMonitorState.MonitoringStopped, lastRun, lastRunUrl, null);
            }

            if (!response.Succeeded || response.Data is null || response.IsStale)
            {
                return Report(
                    ReleaseWorkflowMonitorState.Error,
                    null,
                    null,
                    response.IsStale
                        ? "GitHub is unavailable; cached workflow data cannot confirm the current release status."
                        : response.Message ?? "The release workflow status could not be read from GitHub.");
            }

            if (_time.GetElapsedTime(started) >= _options.MaximumDuration)
            {
                return Report(ReleaseWorkflowMonitorState.TimedOut, lastRun, lastRunUrl,
                    "Monitoring reached its time limit; the release workflow may still be running.");
            }

            var matchingRuns = response.Data.Runs
                .Where(run => IsMatchingRun(run, tag, headSha))
                .ToArray();
            if (matchingRuns.Length > 1)
            {
                return Report(
                    ReleaseWorkflowMonitorState.Error,
                    null,
                    null,
                    "GitHub returned multiple release runs for this tag and commit; no run was selected.");
            }

            if (matchingRuns.Length == 1)
            {
                var run = matchingRuns[0];
                if (!TryValidateRunUrl(identity, run, out var runUrl))
                {
                    return Report(
                        ReleaseWorkflowMonitorState.Error,
                        run,
                        null,
                        "GitHub returned an invalid release workflow link.");
                }

                lastRun = run;
                lastRunUrl = runUrl;
                var state = MapState(run);
                if (state == ReleaseWorkflowMonitorState.Error)
                {
                    return Report(state, run, null,
                        "GitHub returned an unsupported release workflow status.");
                }

                var update = Report(state, run, runUrl, null);
                if (state is ReleaseWorkflowMonitorState.Succeeded
                    or ReleaseWorkflowMonitorState.Failed
                    or ReleaseWorkflowMonitorState.Cancelled)
                {
                    return update;
                }
            }
            else
            {
                Report(ReleaseWorkflowMonitorState.AwaitingRun, null, null,
                    "Waiting for the release workflow run to appear.");
            }

            elapsed = _time.GetElapsedTime(started);
            var remaining = _options.MaximumDuration - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return Report(ReleaseWorkflowMonitorState.TimedOut, lastRun, lastRunUrl,
                    "Monitoring reached its time limit; the release workflow may still be running.");
            }

            var interval = _options.PollIntervals[Math.Min(pollIndex, _options.PollIntervals.Length - 1)];
            pollIndex++;
            try
            {
                await _delay(interval < remaining ? interval : remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Report(ReleaseWorkflowMonitorState.MonitoringStopped, lastRun, lastRunUrl, null);
            }
        }

        ReleaseWorkflowMonitorUpdate Report(
            ReleaseWorkflowMonitorState state,
            GitHubWorkflowRun? run,
            string? runUrl,
            string? message)
        {
            var update = new ReleaseWorkflowMonitorUpdate(
                state,
                run,
                runUrl,
                _time.GetElapsedTime(started),
                message);
            progress?.Report(update);
            return update;
        }
    }

    private static bool IsMatchingRun(GitHubWorkflowRun run, string tag, string headSha)
    {
        var path = run.Path;
        var separator = path?.IndexOf('@');
        if (separator is >= 0)
        {
            path = path![..separator.Value];
        }

        return string.Equals(run.Event, "push", StringComparison.OrdinalIgnoreCase)
            && string.Equals(run.Branch, tag, StringComparison.Ordinal)
            && string.Equals(run.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, WorkflowPath, StringComparison.Ordinal);
    }

    private static ReleaseWorkflowMonitorState MapState(GitHubWorkflowRun run)
    {
        if (string.Equals(run.Status, "queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "requested", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "waiting", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseWorkflowMonitorState.Queued;
        }

        if (string.Equals(run.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseWorkflowMonitorState.Running;
        }

        if (!string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseWorkflowMonitorState.Error;
        }

        return run.Conclusion?.ToLowerInvariant() switch
        {
            "success" => ReleaseWorkflowMonitorState.Succeeded,
            "cancelled" => ReleaseWorkflowMonitorState.Cancelled,
            "failure" or "timed_out" or "action_required" or "startup_failure" => ReleaseWorkflowMonitorState.Failed,
            _ => ReleaseWorkflowMonitorState.Error
        };
    }

    private static bool TryValidateRunUrl(
        GitHubRepositoryIdentity identity,
        GitHubWorkflowRun run,
        out string url)
    {
        url = string.Empty;
        if (run.Id <= 0
            || !Uri.TryCreate(run.HtmlUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            return false;
        }

        var expectedPath = $"/{Uri.EscapeDataString(identity.Owner)}/{Uri.EscapeDataString(identity.Repository)}/actions/runs/{run.Id}";
        if (!string.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    private static bool IsRepositorySegment(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 100
        && value is not "." and not ".."
        && value.IndexOfAny(['/', '\\', '?', '#']) < 0
        && !System.Linq.Enumerable.Any(value, char.IsControl);
}
