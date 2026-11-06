using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Services;
using Velopack;

namespace DevDesk;

public partial class App : Application
{
    private static readonly Task<SettingsService> SettingsInitialization =
        Task.Run(() => SettingsService.Current);

    internal static MainWindow? MainWindowInstance { get; private set; }
    internal static Task ServicesInitialization { get; private set; } = Task.CompletedTask;

    internal static void BeginStartupPreparation() => _ = SettingsInitialization;

    public App()
    {
        // First stage mark of the App object itself, before any XAML exists, so
        // the log shows the App-construction phase instead of jumping from the
        // logging mark straight to the window marks.
        StartupTimeline.Current.Mark(StartupTimeline.AppStarted);
        this.InitializeComponent();

        ServicesInitialization = InitializeServicesWhenReadyAsync();

        // XAML/UI-thread exceptions: log them, but keep default handling.
        // (Setting e.Handled = true would suppress the crash; a template should
        //  not hide bugs, so we re-throw through the default path.)
        this.UnhandledException += (_, e) =>
        {
            LoggingService.Log.Fatal(e.Exception, "Unhandled UI-thread exception");
            e.Handled = false;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTimeline.Current.Mark(StartupTimeline.AppLaunched);
        await SettingsInitialization;
        var savedWindow = SettingsService.Current.Settings.Window;
        var displayTask = Task.Run(() => MainWindow.CurrentDisplays(savedWindow));
        m_window = new MainWindow();
        m_window.RestorePlacement(await displayTask);
        MainWindowInstance = m_window;
        m_window.Activate();

        StartupTimeline.Current.Mark(StartupTimeline.WindowActivated);
        m_window.RecordNativeWindowVisibility();
        m_window.StartUiResponsivenessMonitoring();

        // A second launch is redirected to this instance by SingleInstance; its
        // handler runs off the UI thread, so hop back before touching the window.
        SingleInstance.WatchForActivations(() =>
            m_window?.DispatcherQueue.TryEnqueue(() => m_window.BringToFront()));

        // Fire-and-forget: the update check runs off the UI thread after the
        // window is up, so startup is never blocked by the network.
        _ = CheckForUpdatesWhenReadyAsync();
    }

    private static async Task InitializeServicesWhenReadyAsync()
    {
        await SettingsInitialization.ConfigureAwait(false);
        await Task.Run(() => AppServices.Initialize()).ConfigureAwait(false);
    }

    private async Task CheckForUpdatesWhenReadyAsync()
    {
        await ServicesInitialization.ConfigureAwait(false);
        await CheckForUpdatesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Silent background auto-update flow: check the release feed, download in
    /// the background when an update exists, and only involve the user once it
    /// is ready, asking them to restart.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        var svc = UpdateService.Current;

        if (!svc.IsInstalled)
        {
            LoggingService.Log.Information("Auto-update check skipped: app is not installed");
            return;
        }

        try
        {
            var update = await svc.CheckForUpdatesAsync();
            if (update is null)
            {
                LoggingService.Log.Information("Auto-update: already on the latest version");
                return;
            }

            LoggingService.Log.Information(
                "Auto-update: v{Version} available, downloading in the background",
                update.TargetFullRelease.Version);
            await svc.DownloadUpdatesAsync(update);

            if (m_window is null)
                return;
            m_window.DispatcherQueue.TryEnqueue(() => PromptRestart(update));
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Auto-update check failed");
        }
    }

    private async void PromptRestart(UpdateInfo update)
    {
        try
        {
            var window = m_window;
            if (window?.Content is null)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = window.Content.XamlRoot,
                Title = $"Update v{update.TargetFullRelease.Version} ready",
                Content = "The new version has been downloaded. Restart now to finish the update?",
                PrimaryButtonText = "Restart now",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                UpdateService.Current.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Restart prompt failed");
        }
    }

    private MainWindow? m_window;
}