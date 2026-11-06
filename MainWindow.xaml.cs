using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using DevDesk.Models;
using DevDesk.Pages;
using DevDesk.Services;
using DevDesk.ViewModels;

namespace DevDesk;

public sealed partial class MainWindow : Window
{
    private const uint MonitorDefaultToPrimary = 1;
    private readonly DispatcherQueueTimer _placementTimer;
    private readonly DispatcherQueueTimer _activityTimer;
    private DispatcherQueueTimer? _uiHeartbeatTimer;
    private readonly UiThreadHeartbeat _uiHeartbeat = new();
    private bool _placementRestored;
    private bool _closed;
    private bool _activitySubscribed;
    private bool _routeRestoreQueued;

    public MainWindow()
    {
        this.InitializeComponent();
        StartupTimeline.Current.Mark(StartupTimeline.WindowXamlReady);
        this.Title = "DevDesk";
        this.SystemBackdrop = new MicaBackdrop();

        // Native-feeling, theme-aware title bar: the app content extends into the
        // caption area, Mica shows through it, and the caption buttons (min/max/
        // close) stay OS-drawn with automatic light/dark colors.
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);
        this.AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        this.AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        this.AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        ApplyMinimumSize();

        // Register this window's content as the theme root so ThemeService can
        // flip light/dark from any page. The root assignment is cheap; the
        // actual theme apply walks the whole element tree resolving ThemeResource
        // brushes, so it is deferred until after the window is visible so it
        // cannot block M2 (window visible <400 ms).
        if (this.Content is FrameworkElement root)
            ThemeService.Root = root;
        _placementTimer = DispatcherQueue.CreateTimer();
        _activityTimer = DispatcherQueue.CreateTimer();

        AppServices.Activity.Changed += Activity_Changed;
        _activitySubscribed = true;
        UpdateTitleBarInset();

        AppWindow.Changed += AppWindow_Changed;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        StartupTimeline.Current.Mark(StartupTimeline.WindowSetupReady);
    }

    /// <summary>
    /// UI setup that is not needed before the window is visible: timer
    /// configuration and the initial activity-surface refresh. Deferred off the
    /// M2 critical path so the native window can appear without this work. The
    /// timer objects themselves are created in the constructor; only their
    /// handlers and the first surface refresh run here.
    /// </summary>
    internal void InitializePostVisibility()
    {
        _placementTimer.Interval = TimeSpan.FromMilliseconds(500);
        _placementTimer.IsRepeating = false;
        _placementTimer.Tick += PlacementTimer_Tick;

        _activityTimer.Interval = TimeSpan.FromSeconds(1);
        _activityTimer.IsRepeating = true;
        _activityTimer.Tick += (_, _) => UpdateRunningElapsed();

        RefreshActivitySurfaces();
    }

    /// <summary>Brings this window forward; a second launch redirects here.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
        LoggingService.Log.Information("Existing window brought to the foreground by a second launch");
    }

    public void StartUiResponsivenessMonitoring()
    {
        _uiHeartbeatTimer ??= DispatcherQueue.CreateTimer();
        _uiHeartbeatTimer.Interval = UiThreadHeartbeat.Interval;
        _uiHeartbeatTimer.IsRepeating = true;
        _uiHeartbeatTimer.Tick -= UiHeartbeatTimer_Tick;
        _uiHeartbeatTimer.Tick += UiHeartbeatTimer_Tick;
        _uiHeartbeatTimer.Start();
    }

    private void UiHeartbeatTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        using (PerfCounters.Current.BeginUiOperation("ui responsiveness heartbeat"))
        {
            _uiHeartbeat.Sample();
        }
    }

    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        // No Win32 subclassing: the presenter already owns the resize limits.
        presenter.PreferredMinimumWidth = WindowPlacement.MinimumWidth;
        presenter.PreferredMinimumHeight = WindowPlacement.MinimumHeight;
    }

    internal void RestorePlacement(IReadOnlyList<ScreenRect> displays)
    {
        if (_closed)
        {
            return;
        }

        var saved = SettingsService.Current.Settings.Window;
        StartupTimeline.Current.Mark(StartupTimeline.WindowDisplaysReady);
        var placement = WindowPlacement.Restore(saved, displays);
        var bounds = placement.Bounds;

        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        StartupTimeline.Current.Mark(StartupTimeline.WindowBoundsReady);

        if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        // From here on, changes come from the user and are worth persisting.
        _placementRestored = true;

        LoggingService.Log.Debug(
            "Window placement restored to {Width}x{Height} at {X},{Y} (maximised {Maximized})",
            bounds.Width, bounds.Height, bounds.X, bounds.Y, placement.Maximized);
        StartupTimeline.Current.Mark(StartupTimeline.WindowPlacementReady);
    }

    /// <summary>
    /// The current displays, primary first. An unreadable layout yields an empty
    /// list, which <see cref="WindowPlacement.Restore"/> treats as "no constraint".
    /// </summary>
    internal static IReadOnlyList<ScreenRect> CurrentDisplays(WindowSettings saved)
    {
        try
        {
            var bounds = new NativeRect
            {
                Left = saved.X,
                Top = saved.Y,
                Right = saved.X + saved.Width,
                Bottom = saved.Y + saved.Height,
            };
            var monitor = MonitorFromRect(ref bounds, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero)
            {
                return Array.Empty<ScreenRect>();
            }

            var info = new NativeMonitorInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMonitorInfo>(),
            };
            if (GetMonitorInfo(monitor, ref info))
            {
                return new[]
                {
                    new ScreenRect(
                        info.Work.Left,
                        info.Work.Top,
                        info.Work.Right - info.Work.Left,
                        info.Work.Bottom - info.Work.Top),
                };
            }
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warning(ex, "Could not read the display layout");
        }

        return Array.Empty<ScreenRect>();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "MonitorFromRect")]
    private static extern IntPtr MonitorFromRect(ref NativeRect bounds, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// Records whether the native window handle behind this XAML window is visible
    /// (M2): the WinUI activation mark is a framework-side fact and is not the same
    /// measurement as the native visibility the gate asks for, so both are taken.
    /// </summary>
    internal void RecordNativeWindowVisibility()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var visible = hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
        if (visible)
        {
            StartupTimeline.Current.Mark(StartupTimeline.WindowHwndVisible);
        }
        else
        {
            LoggingService.Log.Warning("Startup: native window handle {Hwnd} is not visible yet", hwnd);
        }
    }

    private void RestoreLastRoute()
    {
        var route = Routes.Normalize(SettingsService.Current.Settings.LastRoute);
        ContentFrame.Navigate(PageFor(route), null, new SuppressNavigationTransitionInfo());
        SelectNavigationItem(route);
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_routeRestoreQueued || args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _routeRestoreQueued = true;
        await App.ServicesInitialization;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed)
                {
                    return;
                }

                RestoreLastRoute();
                StartupTimeline.Current.Mark(StartupTimeline.WindowRouteReady);
            }))
        {
            LoggingService.Log.Warning("Could not queue initial route restoration");
        }
    }

    private static Type PageFor(string tag) => tag switch
    {
        Routes.Projects => typeof(ProjectsPage),
        Routes.Templates => typeof(TemplatesPage),
        Routes.GitHub => typeof(GitHubPage),
        Routes.Self => typeof(DevDeskPage),
        Routes.Settings => typeof(SettingsPage),
        Routes.Diagnostics => typeof(DiagnosticsPage),
        Routes.Activity => typeof(ActivityPage),
        _ => typeof(DashboardPage),
    };

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in RootNavigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag as string == tag)
            {
                RootNavigationView.SelectedItem = navItem;
                return;
            }
        }

        foreach (var item in RootNavigationView.FooterMenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag as string == tag)
            {
                RootNavigationView.SelectedItem = navItem;
                return;
            }
        }
    }

    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is not string tag)
            return;

        Navigate(tag);
    }

    private void Navigate(string tag)
    {
        ContentFrame.Navigate(PageFor(tag), null, new EntranceNavigationTransitionInfo());
        SettingsService.Current.LastRoute = tag;
        SelectNavigationItem(tag);
    }

    // -------------------------------------------------------------------------
    // Activity surfaces (W-0.3.6): the title bar says what is running, the
    // footer badge counts this session's failures, and the flyout shows the last
    // ten. None of them keeps its own copy of the state: they read the centre.
    // -------------------------------------------------------------------------

    private void Activity_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshActivitySurfaces);

    private void RefreshActivitySurfaces()
    {
        var running = AppServices.Activity.Running;

        if (running is null)
        {
            RunningStrip.Visibility = Visibility.Collapsed;
            RunningElapsed.Text = string.Empty;
            _activityTimer.Stop();
        }
        else
        {
            RunningStrip.Visibility = Visibility.Visible;
            RunningGlyph.Glyph = running.Kind switch
            {
                Models.OperationKind.Run => "\uE768",
                Models.OperationKind.Publish => "\uE7B8",
                Models.OperationKind.Test => "\uE9D9",
                _ => "\uE756",
            };
            RunningText.Text = $"{running.KindLabel.ToLowerInvariant()} {running.ProjectName}";
            UpdateRunningElapsed();

            if (!_activityTimer.IsRunning)
            {
                _activityTimer.Start();
            }
        }

        var failures = AppServices.Activity.FailureCount;
        ActivityBadge.Value = failures;
        ActivityBadge.Visibility = failures > 0 ? Visibility.Visible : Visibility.Collapsed;

        var latest = AppServices.Activity.Operations.FirstOrDefault();
        RecentSummary.Text = latest is null
            ? "No runs yet"
            : $"{latest.Title} · {latest.StatusLabel}";
    }

    private void UpdateRunningElapsed()
    {
        var running = AppServices.Activity.Running;
        if (running?.StartedAt is not { } started)
        {
            RunningElapsed.Text = string.Empty;
            return;
        }

        RunningElapsed.Text = PerfCounters.FormatDuration(AppServices.Activity.Time.GetUtcNow() - started);
    }

    private void RunningStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Activity.Running is { } running)
        {
            AppServices.Activity.Cancel(running.Id);
        }
    }

    private void RecentFlyout_Opened(object? sender, object e)
    {
        var viewModel = new ActivityViewModel(AppServices.Activity);
        viewModel.Load();

        var rows = viewModel.Rows.Take(10).ToList();
        RecentList.ItemsSource = rows;
        RecentList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecentEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenActivityButton_Click(object sender, RoutedEventArgs e)
    {
        RecentFlyout.Hide();
        Navigate(Routes.Activity);
    }

    /// <summary>Keeps the running strip clear of the caption buttons at any scale.</summary>
    private void UpdateTitleBarInset()
    {
        try
        {
            var inset = AppWindow.TitleBar.RightInset;
            RunningStrip.Margin = new Thickness(0, 0, Math.Max(140, inset + 12), 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // The default margin is still clear of the buttons on a standard title bar.
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateTitleBarInset();

        if (!_placementRestored || (!args.DidPositionChange && !args.DidSizeChange))
        {
            return;
        }

        _placementTimer.Stop();
        _placementTimer.Start();
    }

    private void PlacementTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _placementTimer.Stop();
        SavePlacement();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        if (_activitySubscribed)
        {
            AppServices.Activity.Changed -= Activity_Changed;
        }

        _activityTimer.Stop();
        _uiHeartbeatTimer?.Stop();

        // Flush the pending debounce: the last resize is often the one that counts.
        _placementTimer.Stop();
        if (_placementRestored)
        {
            SavePlacement();
        }
    }

    private void SavePlacement()
    {
        var state = (AppWindow.Presenter as OverlappedPresenter)?.State ?? OverlappedPresenterState.Restored;

        if (state == OverlappedPresenterState.Minimized)
        {
            // A minimised window reports the shell's icon rectangle; keeping the
            // previous geometry is the only way to restore something sensible.
            return;
        }

        var maximized = state == OverlappedPresenterState.Maximized;
        var window = SettingsService.Current.Settings.Window;

        var position = AppWindow.Position;
        var size = AppWindow.Size;

        var x = maximized ? window.X : position.X;
        var y = maximized ? window.Y : position.Y;
        var width = maximized ? window.Width : size.Width;
        var height = maximized ? window.Height : size.Height;

        if (window.X == x && window.Y == y && window.Width == width
            && window.Height == height && window.Maximized == maximized)
        {
            return;
        }

        window.X = x;
        window.Y = y;
        window.Width = width;
        window.Height = height;
        window.Maximized = maximized;

        SettingsService.Current.Save();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var next = SettingsService.Current.Theme switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System
        };

        SettingsService.Current.Theme = next;
        ThemeService.Apply(next);
    }
}
