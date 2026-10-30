using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace DevDesk.Services;

/// <summary>Outcome of an update check, in words a user can act on.</summary>
public enum UpdateCheckStatus
{
    NotInstalled,
    UpToDate,
    Available,
    Failed,
}

/// <summary>What one check found, plus the sentence the Settings page shows.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, UpdateInfo? Update = null)
{
    public bool HasUpdate => Status == UpdateCheckStatus.Available && Update is not null;
}

/// <summary>
/// Thin wrapper around the Velopack <see cref="UpdateManager"/> so pages stay
/// simple and the update source is chosen in exactly one place.
/// <para>
/// The feed is the repository declared in <c>DevDesk.csproj</c>
/// (<see cref="AppInfo.RepositoryUrl"/>), never a hardcoded URL: moving the
/// repo moves the feed. The channel comes from <see cref="ReleaseChannels"/>,
/// so the updater cannot drift from the tag parser.
/// </para>
/// <para>
/// Nothing here blocks: every entry point is asynchronous, and the manager is
/// built lazily under a lock that guards only object construction (no I/O, no
/// network, no file access). A synchronous <c>Wait()</c> on the UI thread would
/// deadlock against the WinUI dispatcher, so there is none — see
/// <c>UpdateService_DoesNotBlockSynchronously</c>.
/// </para>
/// </summary>
public sealed class UpdateService
{
    public const string NotInstalledMessage =
        "Updates are only available for installed apps. Run setup.exe once, then check again.";

    public const string NoUpdateMessage =
        "You are running the latest version published on the selected release feed.";

    public static UpdateService Current { get; } = new();

    /// <summary>
    /// One clock for every timed update operation. This is a process singleton, so
    /// the clock is not injectable here; the numbers are only ever read by the
    /// Diagnostics card, which is not a timing-sensitive test.
    /// </summary>
    private static readonly TimeProvider Clock = TimeProvider.System;

    private readonly object _gate = new();
    private UpdateManager? _manager;
    private string _channel = ReleaseChannels.Default;

    private UpdateService()
    {
    }

    /// <summary>GitHub Releases feed that hosts this app's updates.</summary>
    public string FeedUrl => AppInfo.Current.RepositoryUrl;

    /// <summary>Update channel: <c>stable</c>, <c>beta</c> (default) or <c>dev</c>.</summary>
    public string Channel
    {
        get
        {
            lock (_gate)
            {
                return _channel;
            }
        }
        set
        {
            var channel = ReleaseChannels.IsKnown(value) ? value : ReleaseChannels.Default;

            lock (_gate)
            {
                if (_channel == channel)
                {
                    return;
                }

                _channel = channel;
                _manager = null; // rebuilt lazily against the new channel
            }

            LoggingService.Log.Information("Update channel set to {Channel}", channel);
        }
    }

    /// <summary>
    /// Whether the app was installed through the Velopack installer.
    /// Running straight from the build output or debugger, updates are unavailable.
    /// </summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                return Manager.IsInstalled;
            }
            catch (Exception ex)
            {
                LoggingService.Log.Warning(ex, "Could not determine whether the app is installed");
                return false;
            }
        }
    }

    public string CurrentVersion
    {
        get
        {
            try
            {
                return Manager.CurrentVersion?.ToString() ?? AppInfo.Current.Version;
            }
            catch
            {
                return AppInfo.Current.Version;
            }
        }
    }

    /// <summary>
    /// Switches channel and persists the choice. The settings write is a file
    /// operation, so it happens off the UI thread.
    /// </summary>
    public async Task SetChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        Channel = channel; // normalises and rebuilds the manager

        var persisted = Channel;
        await Task.Run(() => AppServices.Settings.Channel = persisted, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// One update check, reported as a sentence. Never throws: a failure is a
    /// result the caller can show, because "check for updates" must not crash.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var started = Clock.GetTimestamp();

        if (!IsInstalled)
        {
            LoggingService.Log.Information("Update check skipped: app is not installed");
            PerfCounters.Current.RecordUpdateCheck(
                Clock.GetElapsedTime(started, Clock.GetTimestamp()),
                PerfOutcome.Skipped);
            return new UpdateCheckResult(UpdateCheckStatus.NotInstalled, NotInstalledMessage);
        }

        try
        {
            var update = await Manager.CheckForUpdatesAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(true);
            var elapsed = Clock.GetElapsedTime(started, Clock.GetTimestamp());
            PerfCounters.Current.RecordUpdateCheck(elapsed, PerfOutcome.Ok);

            if (update is null)
            {
                LoggingService.Log.Information("Update check: already on the latest version");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, NoUpdateMessage);
            }

            var version = update.TargetFullRelease.Version.ToString();
            LoggingService.Log.Information("Update check: v{Version} is available", version);

            return new UpdateCheckResult(
                UpdateCheckStatus.Available,
                $"Version {version} is available on the {Channel} feed.",
                update);
        }
        catch (OperationCanceledException)
        {
            PerfCounters.Current.RecordUpdateCheck(
                Clock.GetElapsedTime(started, Clock.GetTimestamp()),
                PerfOutcome.Skipped);
            throw;
        }
        catch (Exception ex)
        {
            PerfCounters.Current.RecordUpdateCheck(
                Clock.GetElapsedTime(started, Clock.GetTimestamp()),
                PerfOutcome.Failed);

            LoggingService.Log.Error(ex, "Update check failed");
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                $"The update check failed: {ex.Message}");
        }
    }

    /// <summary>The background check the app runs at startup; timed like any other.</summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        var started = Clock.GetTimestamp();

        try
        {
            var update = await Manager.CheckForUpdatesAsync().ConfigureAwait(false);
            PerfCounters.Current.RecordUpdateCheck(
                Clock.GetElapsedTime(started, Clock.GetTimestamp()),
                PerfOutcome.Ok);
            return update;
        }
        catch (Exception)
        {
            PerfCounters.Current.RecordUpdateCheck(
                Clock.GetElapsedTime(started, Clock.GetTimestamp()),
                PerfOutcome.Failed);
            throw;
        }
    }

    public Task DownloadUpdatesAsync(UpdateInfo update, Action<int>? progress = null) =>
        Manager.DownloadUpdatesAsync(update, progress, CancellationToken.None);

    /// <summary>
    /// Applies the update and restarts the app. WinUI apps must terminate the
    /// current process so the updater can swap files safely.
    /// </summary>
    public void ApplyUpdatesAndRestart(UpdateInfo update)
    {
        Manager.ApplyUpdatesAndRestart(update);
        Environment.Exit(0);
    }

    private UpdateManager Manager
    {
        get
        {
            lock (_gate)
            {
                if (_manager is not null)
                {
                    return _manager;
                }

                _manager = BuildManager(_channel);
                return _manager;
            }
        }
    }

    private static UpdateManager BuildManager(string channel)
    {
        var options = new UpdateOptions();
        if (channel != ReleaseChannels.Stable)
        {
            options.ExplicitChannel = channel;
        }

        var repoUrl = AppInfo.Current.RepositoryUrl;
        var token = ResolveToken();

        LoggingService.Log.Information(
            "Update source: {RepoUrl} (channel {Channel}, token {HasToken})",
            repoUrl, channel, !string.IsNullOrWhiteSpace(token));

        // accessToken is optional for public repos (unauthenticated GitHub is
        // rate-limited to 60 requests/hour/IP). Set GITHUB_TOKEN to avoid it.
        var source = new GithubSource(
            repoUrl,
            string.IsNullOrWhiteSpace(token) ? null : token,
            prerelease: false);

        return new UpdateManager(source, options, null);
    }

    private static string ResolveToken() =>
        Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
}
