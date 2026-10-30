using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace DevDesk.Services;

/// <summary>
/// Thin wrapper around the Velopack <see cref="UpdateManager"/> so pages stay
/// simple and the update source is chosen in exactly one place.
/// <para>
/// The feed is the repository declared in <c>DevDesk.csproj</c>
/// (<see cref="AppInfo.RepositoryUrl"/>), never a hardcoded URL: moving the
/// repo moves the feed. The channel comes from <see cref="ReleaseChannels"/>,
/// so the updater cannot drift from the tag parser.
/// </para>
/// </summary>
public sealed class UpdateService
{
    public const string NotInstalledMessage =
        "Updates are only available for installed apps. Run setup.exe once, then check again.";

    public const string NoUpdateMessage =
        "You are running the latest version published on the selected release feed.";

    public static UpdateService Current { get; } = new();

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

    public Task<UpdateInfo?> CheckForUpdatesAsync() => Manager.CheckForUpdatesAsync();

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
}
