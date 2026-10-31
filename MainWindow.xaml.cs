using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using DevDesk.Pages;
using DevDesk.Services;
using DevDesk.ViewModels;

namespace DevDesk;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _placementTimer;
    private readonly DispatcherQueueTimer _activityTimer;
    private bool _placementRestored;

    public MainWindow()
    {
        this.InitializeComponent();
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
        RestorePlacement();

        // Register this window's content as the theme root so ThemeService can
        // flip light/dark from any page.
        if (this.Content is FrameworkElement root)
            ThemeService.Root = root;
        ThemeService.Apply(SettingsService.Current.Theme);

        RestoreLastRoute();

        // Geometry is saved on a short debounce: dragging a window raises dozens of
        // moves, and each save is an atomic file write.
        _placementTimer = DispatcherQueue.CreateTimer();
        _placementTimer.Interval = TimeSpan.FromMilliseconds(500);
        _placementTimer.IsRepeating = false;
        _placementTimer.Tick += PlacementTimer_Tick;

        // The running strip reads the centre's clock, never a second stopwatch:
        // the elapsed time on the title bar and the duration in Activity are the
        // same measurement (gotcha 15).
        _activityTimer = DispatcherQueue.CreateTimer();
        _activityTimer.Interval = TimeSpan.FromSeconds(1);
        _activityTimer.IsRepeating = true;
        _activityTimer.Tick += (_, _) => UpdateRunningElapsed();

        AppServices.Activity.Changed += Activity_Changed;
        RefreshActivitySurfaces();
        UpdateTitleBarInset();

        AppWindow.Changed += AppWindow_Changed;
        Closed += MainWindow_Closed;
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

    private void RestorePlacement()
    {
        var saved = SettingsService.Current.Settings.Window;
        var placement = WindowPlacement.Restore(saved, CurrentDisplays());
        var bounds = placement.Bounds;

        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));

        if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        // From here on, changes come from the user and are worth persisting.
        _placementRestored = true;

        LoggingService.Log.Debug(
            "Window placement restored to {Width}x{Height} at {X},{Y} (maximised {Maximized})",
            bounds.Width, bounds.Height, bounds.X, bounds.Y, placement.Maximized);
    }

    /// <summary>
    /// The current displays, primary first. An unreadable layout yields an empty
    /// list, which <see cref="WindowPlacement.Restore"/> treats as "no constraint".
    /// </summary>
    private static IReadOnlyList<ScreenRect> CurrentDisplays()
    {
        var displays = new List<ScreenRect>();

        try
        {
            var primary = DisplayArea.Primary;
            if (primary is not null)
            {
                displays.Add(ToRect(primary.WorkArea));
            }

            foreach (var area in DisplayArea.FindAll())
            {
                var rect = ToRect(area.WorkArea);
                if (!displays.Contains(rect))
                {
                    displays.Add(rect);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warning(ex, "Could not read the display layout");
        }

        return displays;
    }

    private static ScreenRect ToRect(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private void RestoreLastRoute()
    {
        var route = Routes.Normalize(SettingsService.Current.Settings.LastRoute);
        ContentFrame.Navigate(PageFor(route), null, new SuppressNavigationTransitionInfo());
        SelectNavigationItem(route);
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
        AppServices.Activity.Changed -= Activity_Changed;
        _activityTimer.Stop();

        // Flush the pending debounce: the last resize is often the one that counts.
        _placementTimer.Stop();
        SavePlacement();
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
