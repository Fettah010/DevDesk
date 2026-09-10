using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Models;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class DashboardPage : Page
{
    private readonly ProjectScanner _scanner = new();

    public DashboardPage()
    {
        this.InitializeComponent();
        _ = LoadProjectsAsync();
    }

    private async Task LoadProjectsAsync()
    {
        RefreshButton.IsEnabled = false;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        LastRefreshText.Text = "Scanning…";

        try
        {
            var result = await Task.Run(() => _scanner.ScanAsync());
            ApplyScanResult(result.Projects);
            LastRefreshText.Text = "Updated just now";
        }
        catch (Exception)
        {
            LastRefreshText.Text = "Scan failed";
            SubtitleText.Text = "Could not scan the DevEcosystem folder.";
        }
        finally
        {
            BusyRing.IsActive = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyScanResult(IReadOnlyList<ProjectModel> projects)
    {
        var list = new List<ProjectModel>(projects);
        ProjectsGrid.ItemsSource = list;

        var clean = 0;
        var attention = 0;
        var remotes = 0;
        foreach (var p in list)
        {
            if (p.HasRemote)
            {
                remotes++;
            }

            if (p.RemoteOk && !p.Dirty && !p.BehindUpstream)
            {
                clean++;
            }
            else
            {
                attention++;
            }
        }

        ProjectsCountText.Text = list.Count.ToString();
        CleanCountText.Text = clean.ToString();
        AttentionCountText.Text = attention.ToString();
        RemoteCountText.Text = remotes.ToString();

        ProjectsCountPillText.Text = $"{list.Count} in DevEcosystem";
        ScanPathText.Text = $"Scanning {ProjectScanner.DefaultScanRoot} · depth {ProjectScanner.DefaultMaxDepth} · excludes DevDesk";
        SubtitleText.Text = attention == 0
            ? "All projects are healthy."
            : $"{attention} project(s) need attention.";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadProjectsAsync();
    }
}