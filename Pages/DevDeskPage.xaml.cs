using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using DevDesk.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DevDesk.Pages;

/// <summary>
/// The app's own workspace page. State comes from <see cref="SelfViewModel"/>
/// (shared catalog); this file opens folders/files, and — since v0.3.0 — runs the
/// three operations that make DevDesk build itself (W-0.3.8).
/// <para>
/// The page owns no build logic: it picks the verb, hands the session to
/// <see cref="BuildService"/>, and reports what came back. "Rebuild &amp;
/// relaunch" is the one exception in shape only — the rebuild happens in
/// <c>launch-dev.ps1</c> after this window closes, because the running exe and
/// dll are locked (see <see cref="SelfRelaunch"/>).
/// </para>
/// </summary>
public sealed partial class DevDeskPage : Page
{
    private readonly BuildService _builds = AppServices.Builds;
    private readonly GitService _git = new();
    private BuildSession? _session;
    private CancellationTokenSource? _releaseTimelineCancellation;

    public DevDeskPage()
    {
        ViewModel = new SelfViewModel(AppServices.Catalog);
        this.InitializeComponent();
    }

    public SelfViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        var cancellation = new CancellationTokenSource();
        _releaseTimelineCancellation = cancellation;
        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
            await LoadReleaseTimelineAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "DevDesk release timeline could not be loaded");
            ReleaseTimelineState.Text = "Release data could not be loaded.";
        }
        finally
        {
            if (ReferenceEquals(_releaseTimelineCancellation, cancellation))
            {
                _releaseTimelineCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) =>
        _releaseTimelineCancellation?.Cancel();

    private async Task LoadReleaseTimelineAsync(CancellationToken cancellationToken)
    {
        ReleaseTimelineItems.ItemsSource = null;
        ReleaseTimelineItems.Visibility = Visibility.Collapsed;
        OpenReleaseCockpitButton.IsEnabled = ViewModel.HasSelf;
        if (ViewModel.Self is not { } self)
        {
            ReleaseTimelineState.Text = "Release timeline is unavailable because this is an installed build without a local checkout.";
            return;
        }

        var remotes = await _git.GetRemotesAsync(self.Path, cancellationToken);
        if (!remotes.Operation.Succeeded || remotes.Value is null)
        {
            ReleaseTimelineState.Text = "Release timeline unavailable: the local Git remotes could not be read.";
            return;
        }

        var fetchRemote = remotes.Value.FirstOrDefault(remote =>
                string.Equals(remote.Name, "origin", StringComparison.OrdinalIgnoreCase)
                && string.Equals(remote.Direction, "fetch", StringComparison.OrdinalIgnoreCase))
            ?? remotes.Value.FirstOrDefault(remote =>
                string.Equals(remote.Direction, "fetch", StringComparison.OrdinalIgnoreCase));
        var identity = GitHubRepositoryIdentityParser.Parse(fetchRemote?.Url);
        if (identity is not { SupportsPublicApi: true })
        {
            ReleaseTimelineState.Text = "Release timeline unavailable: no supported github.com fetch remote is configured.";
            return;
        }

        var response = await AppServices.GitHub.GetReleasesAsync(identity, cancellationToken: cancellationToken);
        var timeline = ReleaseTimeline.FromResult(response, AppServices.GitHub);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeline.Items.Count > 0)
        {
            ReleaseTimelineItems.ItemsSource = timeline.Items.Select(item => new ReleaseTimelineRow(
                item.Channel,
                item.Name,
                $"{item.Version} · {item.PublishedAt?.ToLocalTime().ToString("g") ?? "date unavailable"}",
                item.Url)).ToArray();
            ReleaseTimelineItems.Visibility = Visibility.Visible;
        }

        var freshness = timeline.IsStale
            ? $"Cached data from {timeline.FetchedAt?.ToLocalTime().ToString("g") ?? "an unknown time"} · stale."
            : timeline.FetchedAt is { } fetchedAt
                ? $"Checked {fetchedAt.ToLocalTime():g}."
                : string.Empty;
        ReleaseTimelineState.Text = string.Join(
            " ",
            new[] { timeline.Message, freshness }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private void OpenReleaseCockpitButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Frame.Navigate(typeof(GitHubPage)))
        {
            ShowWarning("Release cockpit unavailable", "The GitHub release page could not be opened.");
        }
    }

    private async void ReleaseTimelineLink_Click(object sender, RoutedEventArgs e)
    {
        var value = (sender as FrameworkElement)?.Tag as string;
        if (!AppServices.GitHub.TryCreateWebUri(value, out var uri) || uri is null)
        {
            ShowWarning("Release link unavailable", "GitHub returned a link that DevDesk will not open.");
            return;
        }

        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warning(ex, "Could not open a published DevDesk release");
            ShowWarning("Could not open release", "The release page could not be opened in the browser.");
        }
    }

    private void OpenExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOpenActions.OpenInExplorer(ViewModel.SelfPath))
        {
            return;
        }

        ShowWarning("Nothing to open", "The DevDesk repo folder was not found.");
    }

    // The roadmap, not the retired plan: this link must point at a file that
    // exists, or it is a dead button that looks live.
    private void OpenPlanButton_Click(object sender, RoutedEventArgs e)
    {
        var roadmapPath = Path.Combine(ViewModel.SelfPath, "docs", "DEVDESK-ROADMAP.md");
        if (ProjectOpenActions.OpenWithDefaultApp(roadmapPath))
        {
            return;
        }

        ShowWarning("Roadmap not found", roadmapPath);
    }

    private async void BuildSelfButton_Click(object sender, RoutedEventArgs e) =>
        await StartSelfAsync(OperationKind.Build);

    private async void RunTestsButton_Click(object sender, RoutedEventArgs e) =>
        await StartSelfAsync(OperationKind.Test);

    private void StopSelfButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session)
        {
            AppServices.Activity.Cancel(session.OperationId);
        }
    }

    private async Task StartSelfAsync(OperationKind kind)
    {
        if (ViewModel.Self is not { } self)
        {
            ShowWarning(
                "No checkout to build",
                "DevDesk is running from an installed build, so there is no repository here to build. Start it from the repository to use this page.");
            return;
        }

        if (_session is { } active && !active.Completion.IsCompleted)
        {
            ShowWarning("Already running", "Wait for the current run to finish, or press Stop.");
            return;
        }

        var settings = SettingsService.Current.Settings;
        BuildSession session;
        string label;

        if (kind == OperationKind.Test)
        {
            session = _builds.TestSelf(self, settings, SelfConsole);
            label = $"Test {self.Name} · dotnet test DevDesk.Tests -p:Platform=x64";
        }
        else
        {
            var resolution = BuildService.ResolveProfile(self, settings);
            if (resolution.Profile is not { } profile)
            {
                ShowWarning("Nothing to build", resolution.Reason ?? "This folder has no buildable target.");
                return;
            }

            session = _builds.Build(self, profile, SelfConsole);
            label = $"Build {profile.TargetName} · {profile.Summary}";
        }

        SelfConsole.Clear();
        SetRunning(true, $"{label} · running…");
        _session = session;

        try
        {
            var finished = await session.Completion;
            ApplyOutcome(finished, self);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "{Kind} of DevDesk itself failed", kind);
            ShowWarning($"{kind} failed", ex.Message);
        }
        finally
        {
            SetRunning(false, null);
        }
    }

    /// <summary>
    /// The outcome in the page's own words, plus the one explanation a self-build
    /// needs: MSBuild cannot overwrite the exe this window is running from.
    /// </summary>
    private void ApplyOutcome(Operation operation, ProjectModel self)
    {
        SelfBuildStatus.Text = operation.Status switch
        {
            OperationStatus.Succeeded =>
                $"{operation.KindLabel} {self.Name} succeeded in {operation.DurationText} · exit {operation.ExitCode ?? 0}",
            OperationStatus.Failed =>
                $"{operation.KindLabel} {self.Name} failed in {operation.DurationText}"
                + (operation.ExitCode is { } code ? $" · exit {code}" : string.Empty),
            OperationStatus.Canceled =>
                $"{operation.KindLabel} {self.Name} cancelled after {operation.DurationText} · not a failure",
            _ => $"{operation.KindLabel} {self.Name} · {operation.StatusLabel}",
        };

        SelfBuildStatus.Foreground = (Brush)Application.Current.Resources[operation.Status switch
        {
            OperationStatus.Succeeded => "SystemFillColorSuccessBrush",
            OperationStatus.Failed => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        }];

        ShowOutputButton.IsEnabled = true;

        if (operation.Status == OperationStatus.Failed && IsLockedOutput(operation))
        {
            ShowWarning(
                "The running app locks its own output",
                "Windows keeps bin\\…\\DevDesk.exe and DevDesk.dll open while DevDesk runs, so MSBuild could not copy over them. "
                + "Use Rebuild & relaunch: it closes this window, builds, and reopens the new build.");
        }
    }

    private static bool IsLockedOutput(Operation operation)
    {
        var text = operation.FirstError ?? operation.Hint ?? string.Empty;
        return text.Contains("MSB3027", StringComparison.OrdinalIgnoreCase)
            || text.Contains("MSB3026", StringComparison.OrdinalIgnoreCase)
            || text.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);
    }

    private void SetRunning(bool running, string? status)
    {
        SelfBuildRing.IsActive = running;
        SelfBuildRing.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopSelfButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        BuildSelfButton.IsEnabled = !running;
        RunTestsButton.IsEnabled = !running;
        LaunchButton.IsEnabled = !running;

        if (status is not null)
        {
            SelfBuildStatus.Text = status;
            SelfBuildStatus.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
    }

    // ---------------------------------------------------------------------
    // Rebuild & relaunch. The launcher waits for this pid, then builds and
    // reopens the app; this process only has to hand over the id and go.
    // ---------------------------------------------------------------------

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Self is not { } self)
        {
            ShowWarning(
                "Nothing to relaunch",
                "DevDesk is running from an installed build; the launcher that rebuilds a checkout is not part of it.");
            return;
        }

        var launcher = SelfRelaunch.LauncherPath(self.Path);
        if (!File.Exists(launcher))
        {
            ShowWarning(
                "Launcher not found",
                $"{launcher} is missing, so DevDesk cannot rebuild itself from here. Use the desktop shortcut instead.");
            return;
        }

        if (!await ConfirmRelaunchAsync(launcher))
        {
            return;
        }

        if (!SelfRelaunch.TryStart(self.Path, Environment.ProcessId, out var error))
        {
            ShowWarning("Relaunch not started", error ?? "The launcher could not be started.");
            return;
        }

        Application.Current.Exit();
    }

    private async Task<bool> ConfirmRelaunchAsync(string launcher)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Rebuild & relaunch?",
            Content = new TextBlock
            {
                Text = "DevDesk closes now. The launcher waits for this window, rebuilds only if a source changed, "
                    + "and opens the new build.\n\n"
                    + $"Launcher: {launcher}\nWait for process id: {Environment.ProcessId}",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Rebuild & relaunch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowWarning(string title, string message)
    {
        ActionBar.Severity = InfoBarSeverity.Warning;
        ActionBar.Title = title;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    private sealed record ReleaseTimelineRow(string Channel, string Title, string Detail, string Url);
}
