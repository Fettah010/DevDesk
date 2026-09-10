using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class DevDeskPage : Page
{
    private readonly ProjectScanner _scanner = new();

    public DevDeskPage()
    {
        this.InitializeComponent();
        _ = LoadSelfAsync();
    }

    private async Task LoadSelfAsync()
    {
        RefreshSelfButton.IsEnabled = false;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;

        try
        {
            var result = await Task.Run(() => _scanner.ScanAsync());
            var self = result.Self;

            if (self is null)
            {
                SelfNameText.Text = "DevDesk";
                SelfStatusText.Text = "Self workspace not found (this repo is not under the scan root).";
                SelfCommitsGrid.ItemsSource = null;
                ComponentsCard.Visibility = Visibility.Collapsed;
                return;
            }

            SelfNameText.Text = self.Name;
            SelfVersionText.Text = self.VersionDisplay;
            SelfBranchText.Text = self.Branch;
            SelfStatusText.Text = self.ListSubtitle;
            SelfPathText.Text = self.Path;
            SelfCommitsGrid.ItemsSource = self.RecentCommits;
            SelfComponentsList.ItemsSource = self.Components;
            ComponentsCard.Visibility = self.Components.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            SelfStatusText.Text = "Could not read the repo state.";
        }
        finally
        {
            BusyRing.IsActive = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
            RefreshSelfButton.IsEnabled = true;
        }
    }

    private async void RefreshSelfButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadSelfAsync();
    }

    private void OpenExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOpenActions.OpenInExplorer(_scanner.SelfPath))
        {
            return;
        }

        ActionBar.Severity = InfoBarSeverity.Warning;
        ActionBar.Title = "Nothing to open";
        ActionBar.Message = "The DevDesk repo folder was not found.";
        ActionBar.IsOpen = true;
    }

    private void OpenPlanButton_Click(object sender, RoutedEventArgs e)
    {
        var planPath = Path.Combine(_scanner.SelfPath, "docs", "DEVDESK-PLAN.md");
        if (ProjectOpenActions.OpenWithDefaultApp(planPath))
        {
            return;
        }

        ActionBar.Severity = InfoBarSeverity.Warning;
        ActionBar.Title = "Plan doc not found";
        ActionBar.Message = planPath;
        ActionBar.IsOpen = true;
    }

    // Phase 1 (GitService / BuildService) wires these to real actions.
    private void StubAction_Click(object sender, RoutedEventArgs e)
    {
        ActionBar.Severity = InfoBarSeverity.Warning;
        ActionBar.Title = "Not wired yet";
        ActionBar.Message = "Page actions arrive with the Phase 1 services (GitService / BuildService).";
        ActionBar.IsOpen = true;
    }
}