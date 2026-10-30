using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Models;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class ProjectsPage : Page
{
    public ProjectsPage()
    {
        this.InitializeComponent();
        _ = LoadProjectsAsync();
    }

    private async Task LoadProjectsAsync()
    {
        try
        {
            var snapshot = await AppServices.Catalog.GetOrRefreshAsync();
            ProjectsList.ItemsSource = snapshot.Projects;
            if (ProjectsList.Items.Count > 0)
                ProjectsList.SelectedIndex = 0;
        }
        catch (System.Exception ex)
        {
            NotWiredInfoBar.Severity = InfoBarSeverity.Error;
            NotWiredInfoBar.Title = "Could not read the project list";
            NotWiredInfoBar.Message = ex.Message;
            NotWiredInfoBar.IsOpen = true;
        }
    }

    private void ProjectsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectsList.SelectedItem is not ProjectModel p)
            return;

        DetailIcon.Glyph = p.IconGlyph;
        DetailTitle.Text = p.Name;
        DetailPath.Text = p.Path;
        DetailWorkspaceBadge.Visibility = p.IsWorkspace ? Visibility.Visible : Visibility.Collapsed;

        OverviewBranch.Text = p.Branch;
        OverviewVersion.Text = p.VersionDisplay;
        OverviewRepo.Text = p.HasRemote ? $"{p.RemoteHost} — remote configured" : "Local only — add a remote in Phase 2";
        OverviewTree.Text = p.Dirty ? "dirty — uncommitted changes present" : "clean";
        OverviewCommit.Text = p.RecentCommits.FirstOrDefault()?.Subject ?? "—";
    }

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OverviewPanel.Visibility = DetailTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommitsPanel.Visibility = DetailTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ReleasesPanel.Visibility = DetailTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ActionsPanel.Visibility = DetailTabs.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProjectAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string action)
            return;

        NotWiredInfoBar.Title = $"'{action}' is a placeholder";
        NotWiredInfoBar.Message = $"BuildService / GitService land in Phase 1 — this will run without the terminal.";
        NotWiredInfoBar.IsOpen = true;
    }
}