using System;
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

    /// <summary>The project the detail pane is showing, for the open actions.</summary>
    private ProjectModel? Selected => ProjectsList.SelectedItem as ProjectModel;

    private async Task LoadProjectsAsync()
    {
        try
        {
            var snapshot = await AppServices.Catalog.GetOrRefreshAsync();
            ProjectsList.ItemsSource = snapshot.Projects;
            if (ProjectsList.Items.Count > 0)
                ProjectsList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ShowError("Could not read the project list", ex.Message);
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

        // "No remote" is a fact about this folder, not a promise about a future
        // version: a workspace like this one has no remote and works fine.
        OverviewRepo.Text = p.HasRemote
            ? $"{p.RemoteHost} — remote configured"
            : "No remote — everything here is local";

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

    // The two real actions on this page. They open what already exists on disk
    // through ProjectOpenActions, exactly like the Dashboard card does; nothing
    // here writes to the repository.
    private void OpenSolutionButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null)
            return;

        var target = ProjectOpenActions.FindSolutionOrProject(project.Path);
        if (target is null)
        {
            ShowError(
                "Nothing to open",
                $"{project.Name} has no solution or project file at its root.");
            return;
        }

        if (!ProjectOpenActions.OpenWithDefaultApp(target))
        {
            ShowError("Could not open the project", target);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null)
            return;

        if (!ProjectOpenActions.OpenInExplorer(project.Path))
        {
            ShowError("Could not open the folder", project.Path);
        }
    }

    private void ShowError(string title, string message)
    {
        PageInfoBar.Severity = InfoBarSeverity.Error;
        PageInfoBar.Title = title;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }
}
