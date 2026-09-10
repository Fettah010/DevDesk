using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Models;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class DashboardPage : Page
{
    private List<ProjectModel> _projects = new();

    public DashboardPage()
    {
        this.InitializeComponent();
        LoadSampleProjects();
    }

    // PLACEHOLDER: consumes SampleDataService until the real ProjectScanner
    // lands in Phase 0. The card layout is final; only the data source changes.
    private void LoadSampleProjects()
    {
        _projects = new List<ProjectModel>(SampleDataService.GetAll());
        ProjectsGrid.ItemsSource = _projects;

        var clean = 0;
        var attention = 0;
        var remotes = 0;
        foreach (var p in _projects)
        {
            if (p.HasRemote)
                remotes++;
            if (p.RemoteOk && !p.Dirty && !p.BehindUpstream)
                clean++;
            else
                attention++;
        }

        ProjectsCountText.Text = _projects.Count.ToString();
        CleanCountText.Text = clean.ToString();
        AttentionCountText.Text = attention.ToString();
        RemoteCountText.Text = remotes.ToString();

        ProjectsCountPillText.Text = $"{_projects.Count} in C:\\Code\\projects";
        ScanPathText.Text = "Scanning C:\\Code\\projects · depth 3";
        LastRefreshText.Text = "Updated just now";
        SubtitleText.Text = attention == 0
            ? "All projects are healthy."
            : $"{attention} project(s) need attention.";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSampleProjects();
    }
}