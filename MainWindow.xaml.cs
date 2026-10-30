using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using DevDesk.Pages;
using DevDesk.Services;

namespace DevDesk;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _placementTimer;
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

        ContentFrame.Navigate(PageFor(tag), null, new EntranceNavigationTransitionInfo());
        SettingsService.Current.LastRoute = tag;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
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
