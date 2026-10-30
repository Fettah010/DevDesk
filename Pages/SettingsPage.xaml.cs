using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class SettingsPage : Page
{
    // Selecting an index in code raises SelectionChanged. Without this guard the
    // page would rewrite settings every time it is opened (and would overwrite a
    // hand-edited channel with whatever the control happened to show).
    private bool _initialising = true;

    public SettingsPage()
    {
        this.InitializeComponent();

        var settings = SettingsService.Current;

        VersionText.Text = $"DevDesk {AppInfo.Current.DisplayVersion}";
        SelectThemeIndex(settings.Theme);

        // Both values are what the scanner actually uses, not what the settings
        // file happens to say: a hand-edited root or ignore is not applied until
        // 0.7.0, and showing it as if it were would be a lie.
        ScanRootsText.Text = $"{AppServices.Catalog.ScanRoot}  (depth {ScanEngine.DefaultMaxDepth})";
        IgnoresText.Text = string.Join(" · ", PathHygiene.IgnoredFolderNames);

        SelectChannelIndex(settings.Channel);
        _initialising = false;

        if (settings.LastLoad.HasWarning)
        {
            ShowInfoBar(settings.LastLoad.Warning!, InfoBarSeverity.Warning);
        }
    }

    private static int ThemeIndex(AppTheme theme) => theme switch
    {
        AppTheme.Light => 1,
        AppTheme.Dark => 2,
        _ => 0
    };

    private static int ChannelIndex(string channel) => channel switch
    {
        ReleaseChannels.Stable => 0,
        ReleaseChannels.Dev => 2,
        _ => 1 // beta is the default before 1.0 (D5)
    };

    private void SelectThemeIndex(AppTheme theme) => ThemeSegment.SelectedIndex = ThemeIndex(theme);

    private void SelectChannelIndex(string channel) => ChannelSegment.SelectedIndex = ChannelIndex(channel);

    // Functional today: flipping the segmented control re-themes the whole app
    // and persists the choice so the next launch starts on the same theme.
    private void ThemeSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising)
        {
            return;
        }

        var theme = ThemeSegment.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System
        };

        SettingsService.Current.Theme = theme;
        ThemeService.Apply(theme);
    }

    // The channel is persisted in settings and handed to the updater, which
    // rebuilds its Velopack manager against the new feed on the next check.
    private void ChannelSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising)
        {
            return;
        }

        var channel = ChannelSegment.SelectedIndex switch
        {
            0 => ReleaseChannels.Stable,
            2 => ReleaseChannels.Dev,
            _ => ReleaseChannels.Beta
        };

        SettingsService.Current.Channel = channel;
        UpdateService.Current.Channel = channel;

        ShowInfoBar($"Update channel set to {channel}.", InfoBarSeverity.Success);
    }

    private void SaveTokenButton_Click(object sender, RoutedEventArgs e) => ShowInfoBar(
        "Token storage lands in Phase 2 — it will be kept in Windows Credential Manager.");

    private void AddRootButton_Click(object sender, RoutedEventArgs e) => ShowInfoBar(
        "Multiple scan roots land in 0.7.0. The single root is editable in settings.json.");

    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => ShowInfoBar(
        $"Auto-update runs silently at startup (Velopack) from the {UpdateService.Current.Channel} feed.");

    private void ShowInfoBar(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }
}
