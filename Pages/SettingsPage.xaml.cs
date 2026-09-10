using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        this.InitializeComponent();

        VersionText.Text = $"DevDesk {AppInfo.Current.Version}";
        SelectThemeIndex(SettingsService.Current.Theme);
    }

    private static int ThemeIndex(AppTheme theme) => theme switch
    {
        AppTheme.Light => 1,
        AppTheme.Dark => 2,
        _ => 0
    };

    private void SelectThemeIndex(AppTheme theme) => ThemeSegment.SelectedIndex = ThemeIndex(theme);

    // Functional today: flipping the segmented control re-themes the whole app
    // and persists the choice so the next launch starts on the same theme.
    private void ThemeSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var theme = ThemeSegment.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System
        };

        SettingsService.Current.Theme = theme;
        ThemeService.Apply(theme);
    }

    private void SaveTokenButton_Click(object sender, RoutedEventArgs e) => ShowInfoBar(
        "Token storage lands in Phase 2 — it will be kept in Windows Credential Manager.");

    private void AddRootButton_Click(object sender, RoutedEventArgs e) => ShowInfoBar(
        "Multiple scan roots land in Phase 1.");

    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => ShowInfoBar(
        "Auto-update already runs silently at startup (Velopack). Installed apps check the stable feed.");

    private void ShowInfoBar(string message)
    {
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }
}