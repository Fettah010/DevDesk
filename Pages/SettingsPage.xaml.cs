using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using DevDesk.Models;
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
        RefreshRootList();
        IgnoresText.Text = string.Join(" · ", PathHygiene.IgnoredFolderNames);

        SelectChannelIndex(settings.Channel);
        _initialising = false;
        RefreshGitHubCredentialStatus();

        if (settings.LastLoad.HasWarning)
        {
            ShowInfoBar(settings.LastLoad.Warning!, InfoBarSeverity.Warning);
        }
    }

    private ScanRootSettings? SelectedRoot => RootList.SelectedItem as ScanRootSettings;

    private void RefreshRootList(int selectedIndex = 0)
    {
        RootList.ItemsSource = null;
        RootList.ItemsSource = SettingsService.Current.Settings.Roots;
        if (RootList.Items.Count == 0)
        {
            RootStatusText.Text = "No scan roots are configured.";
            RootDepthBox.IsEnabled = false;
            RootIgnoresBox.IsEnabled = false;
            SaveRootSettingsButton.IsEnabled = false;
            return;
        }

        RootList.SelectedIndex = Math.Clamp(selectedIndex, 0, RootList.Items.Count - 1);
        ShowSelectedRoot();
    }

    private void RootList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedRoot();

    private void ShowSelectedRoot()
    {
        if (SelectedRoot is not { } root)
        {
            return;
        }

        RootDepthBox.Text = root.Depth.ToString();
        RootIgnoresBox.Text = string.Join(Environment.NewLine, root.Ignores);
        var state = root.Enabled ? "Enabled" : "Disabled";
        var location = root.Path.StartsWith(@"\\", StringComparison.Ordinal)
            ? " · Network path; scan may be slower or unavailable"
            : string.Empty;
        var existence = root.Enabled && !Directory.Exists(root.Path)
            ? " · Missing or inaccessible"
            : string.Empty;
        RootStatusText.Text = $"{state} · depth {root.Depth} · {root.Path}{location}{existence}";
        RootDepthBox.IsEnabled = true;
        RootIgnoresBox.IsEnabled = true;
        SaveRootSettingsButton.IsEnabled = true;
    }

    private async void AddRootButton_Click(object sender, RoutedEventArgs e)
    {
        var owner = App.MainWindowInstance;
        if (owner is null)
        {
            ShowInfoBar("The folder picker is unavailable until the main window is ready.", InfoBarSeverity.Error);
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(owner));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(folder.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            ShowInfoBar($"That folder path is not valid: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        var roots = SettingsService.Current.Settings.Roots;
        if (roots.Any(root => PathsEqual(root.Path, path)))
        {
            ShowInfoBar("That scan root is already configured.", InfoBarSeverity.Warning);
            return;
        }

        roots.Add(new ScanRootSettings { Path = path });
        SettingsService.Current.Save();
        RefreshRootList(roots.Count - 1);
        await RefreshCatalogAsync("Scan root added.");
    }

    private async void ToggleRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedRootIndex(out var index, out var roots))
        {
            return;
        }

        roots[index].Enabled = !roots[index].Enabled;
        SettingsService.Current.Save();
        RefreshRootList(index);
        await RefreshCatalogAsync(roots[index].Enabled ? "Scan root enabled." : "Scan root disabled.");
    }

    private async void MoveRootUpButton_Click(object sender, RoutedEventArgs e) =>
        await MoveRootAsync(-1);

    private async void MoveRootDownButton_Click(object sender, RoutedEventArgs e) =>
        await MoveRootAsync(1);

    private async Task MoveRootAsync(int offset)
    {
        if (!TrySelectedRootIndex(out var index, out var roots))
        {
            return;
        }

        var destination = index + offset;
        if (destination < 0 || destination >= roots.Count)
        {
            return;
        }

        (roots[index], roots[destination]) = (roots[destination], roots[index]);
        SettingsService.Current.Save();
        RefreshRootList(destination);
        await RefreshCatalogAsync("Scan root order updated.");
    }

    private async void RemoveRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedRootIndex(out var index, out var roots))
        {
            return;
        }

        var root = roots[index];
        if (roots.Count == 1)
        {
            ShowInfoBar("At least one root must stay configured. Disable it or add another root instead.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove scan root?",
            Content = $"This removes {root.Path} from future scans. Its files and repositories will not be changed.",
            PrimaryButtonText = "Remove root",
            CloseButtonText = "Keep root",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        roots.RemoveAt(index);
        SettingsService.Current.Save();
        RefreshRootList(Math.Max(0, index - 1));
        await RefreshCatalogAsync("Scan root removed.");
    }

    private async void SaveRootSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedRootIndex(out var index, out var roots))
        {
            return;
        }

        if (!int.TryParse(RootDepthBox.Text, out var depth) || depth is < 1 or > 8)
        {
            ShowInfoBar("Scan depth must be a whole number from 1 to 8.", InfoBarSeverity.Warning);
            return;
        }

        var patterns = RootIgnoresBox.Text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var invalid = patterns.FirstOrDefault(pattern => !IgnoreRuleMatcher.IsValidPattern(pattern));
        if (invalid is not null)
        {
            ShowInfoBar($"Invalid ignore rule: {invalid}", InfoBarSeverity.Warning);
            return;
        }

        roots[index].Depth = depth;
        roots[index].Ignores = patterns;
        SettingsService.Current.Save();
        RefreshRootList(index);
        await RefreshCatalogAsync("Root settings saved.");
    }

    private void PreviewIgnoreButton_Click(object sender, RoutedEventArgs e)
    {
        var path = IgnorePreviewPathBox.Text.Trim();
        if (SelectedRoot is not { } root
            || string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Replace('\\', '/').Split('/').Any(segment => segment is "." or ".."))
        {
            IgnorePreviewResultText.Text = "Enter a non-empty path relative to the selected root. Absolute paths and traversal segments are not allowed.";
            return;
        }

        var decision = new IgnoreRuleMatcher(root.Ignores).Evaluate(path);
        IgnorePreviewResultText.Text = decision.IsSafetyExcluded
            ? $"{decision.RelativePath} is always excluded by the path-safety boundary."
            : decision.IsExcluded
                ? $"{decision.RelativePath} is excluded by '{decision.MatchingRule}'."
                : decision.MatchingRule is null
                    ? $"{decision.RelativePath} is not excluded by the selected root's ignore rules."
                    : $"{decision.RelativePath} is included by '{decision.MatchingRule}'.";
    }

    private bool TrySelectedRootIndex(out int index, out List<ScanRootSettings> roots)
    {
        roots = SettingsService.Current.Settings.Roots;
        index = RootList.SelectedIndex;
        return index >= 0 && index < roots.Count;
    }

    private async Task RefreshCatalogAsync(string successMessage)
    {
        try
        {
            AppServices.ReconfigureRootWatcher();
            await AppServices.Catalog.RefreshAsync();
            ShowInfoBar(successMessage, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Project catalog refresh failed after root settings changed");
            ShowInfoBar($"Settings were saved, but the project scan failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
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
