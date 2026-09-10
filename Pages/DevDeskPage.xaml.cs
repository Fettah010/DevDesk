using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class DevDeskPage : Page
{
    public DevDeskPage()
    {
        this.InitializeComponent();
        _ = LoadSelfAsync();
    }

    // Simulates the (future) git/scan read long enough to show the loading
    // overlay; the real GitService will drive the same BusyOverlay.
    private async Task LoadSelfAsync()
    {
        RefreshSelfButton.IsEnabled = false;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;

        await Task.Delay(500);

        var self = SampleDataService.GetSelf();
        SelfNameText.Text = self.Name;
        SelfVersionText.Text = self.VersionDisplay;
        SelfBranchText.Text = self.Branch;
        SelfStatusText.Text = self.ListSubtitle;
        SelfPathText.Text = self.Path;
        SelfCommitsGrid.ItemsSource = self.RecentCommits;
        SelfComponentsList.ItemsSource = self.Components;
        ComponentsCard.Visibility = self.Components.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        BusyRing.IsActive = false;
        BusyOverlay.Visibility = Visibility.Collapsed;
        RefreshSelfButton.IsEnabled = true;
    }

    private async void RefreshSelfButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadSelfAsync();
    }

    // Phase 1 (GitService / BuildService) wires these to real actions.
    private void StubAction_Click(object sender, RoutedEventArgs e)
    {
        ActionBar.IsOpen = true;
    }
}