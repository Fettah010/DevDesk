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
        RefreshGitHubCredentialStatus();

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

        // Same reason as the channel below: layout selection is not a user choice,
        // and re-applying it on every visit would be a settings write per visit.
        if (theme == SettingsService.Current.Theme)
        {
            return;
        }

        SettingsService.Current.Theme = theme;
        ThemeService.Apply(theme);
    }

    // The channel is persisted in settings and handed to the updater, which
    // rebuilds its Velopack manager against the new feed on the next check.
    //
    // The "is it different" test is not a micro-optimisation: a Segmented control
    // raises SelectionChanged for its own layout selection after the page is
    // shown, and _initialising is already false by then. Without the comparison,
    // merely opening Settings announced "update channel set to beta" for a change
    // nobody made — a fabricated confirmation.
    private async void ChannelSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        if (channel == UpdateService.Current.Channel)
        {
            return;
        }

        // async void: nothing above can throw except the settings write, which
        // would otherwise be an unhandled exception on the UI thread.
        try
        {
            await UpdateService.Current.SetChannelAsync(channel);
            ShowInfoBar($"Update channel set to {channel}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Switching the update channel to {Channel} failed", channel);
            ShowInfoBar($"The channel could not be saved: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // The one Settings action that is real today: it asks the updater and reports
    // what actually came back, including "not installed" from the build output.
    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateSpinner.IsActive = true;
        UpdateSpinner.Visibility = Visibility.Visible;

        try
        {
            var result = await UpdateService.Current.CheckAsync();

            ShowInfoBar(
                result.Message,
                result.Status switch
                {
                    UpdateCheckStatus.UpToDate => InfoBarSeverity.Success,
                    UpdateCheckStatus.Failed => InfoBarSeverity.Error,
                    _ => InfoBarSeverity.Informational
                });
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "The update check threw");
            ShowInfoBar($"The update check failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            UpdateSpinner.IsActive = false;
            UpdateSpinner.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void SaveTokenButton_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password;
        SaveTokenButton.IsEnabled = false;
        SignOutButton.IsEnabled = false;
        TokenStatusText.Text = "Validating token…";

        try
        {
            var result = await AppServices.GitHub.SaveAndValidateTokenAsync(token);
            if (result.Succeeded && result.Data is not null)
            {
                TokenStatusText.Text = $"Signed in as {result.Data.Login}.";
                ShowInfoBar(
                    $"Token validated for {result.Data.Login} and stored securely. Repository access is checked separately.",
                    InfoBarSeverity.Success);
            }
            else
            {
                TokenStatusText.Text = "Token was not saved.";
                ShowInfoBar(result.Message ?? "GitHub could not validate the token.", InfoBarSeverity.Error);
            }
        }
        catch (Exception)
        {
            LoggingService.Log.Warning("GitHub token validation or secure storage failed");
            TokenStatusText.Text = "Credential storage is unavailable.";
            ShowInfoBar("The token could not be validated or stored securely.", InfoBarSeverity.Error);
        }
        finally
        {
            TokenBox.Password = string.Empty;
            SaveTokenButton.IsEnabled = true;
            RefreshGitHubCredentialStatus();
        }
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        SignOutButton.IsEnabled = false;
        SaveTokenButton.IsEnabled = false;
        try
        {
            await AppServices.GitHub.SignOutAsync();
            TokenStatusText.Text = "Not signed in; public repository data can still be read.";
            ShowInfoBar("GitHub token and cached repository data were removed.", InfoBarSeverity.Success);
        }
        catch (Exception)
        {
            LoggingService.Log.Warning("GitHub sign-out could not remove all local credential data");
            ShowInfoBar("Sign-out could not remove all protected credential or cache data.", InfoBarSeverity.Error);
        }
        finally
        {
            SaveTokenButton.IsEnabled = true;
            RefreshGitHubCredentialStatus();
        }
    }

    private void RefreshGitHubCredentialStatus()
    {
        try
        {
            var signedIn = AppServices.GitHub.HasToken;
            SignOutButton.IsEnabled = signedIn;
            if (!signedIn)
            {
                TokenStatusText.Text = "Not signed in; public repository data can still be read.";
            }
            else if (!TokenStatusText.Text.StartsWith("Signed in as ", StringComparison.Ordinal))
            {
                TokenStatusText.Text = "A protected token is stored; account details load on the GitHub page.";
            }
        }
        catch (Exception)
        {
            SignOutButton.IsEnabled = false;
            TokenStatusText.Text = "Secure credential storage is unavailable.";
        }
    }

    private void ShowInfoBar(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }
}
