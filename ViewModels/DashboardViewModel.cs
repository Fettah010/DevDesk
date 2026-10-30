using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDesk.Models;
using DevDesk.Services;
using Microsoft.UI.Xaml;

namespace DevDesk.ViewModels;

/// <summary>
/// Dashboard state. It never owns a scanner: it reads <see cref="ProjectCatalog"/>,
/// so arriving on the page reuses the snapshot the previous visit produced.
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly ProjectCatalog _catalog;

    [ObservableProperty]
    public partial IReadOnlyList<ProjectModel> Projects { get; set; }

    [ObservableProperty]
    public partial string ProjectCountText { get; set; }

    [ObservableProperty]
    public partial string CleanCountText { get; set; }

    [ObservableProperty]
    public partial string AttentionCountText { get; set; }

    [ObservableProperty]
    public partial string RemoteCountText { get; set; }

    [ObservableProperty]
    public partial string Subtitle { get; set; }

    [ObservableProperty]
    public partial string LastRefreshText { get; set; }

    [ObservableProperty]
    public partial string ProjectsPillText { get; set; }

    [ObservableProperty]
    public partial string ScanPathText { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; }

    private readonly IProgress<ScanProgress> _progress;

    public DashboardViewModel(ProjectCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        Projects = Array.Empty<ProjectModel>();
        ProjectCountText = "0";
        CleanCountText = "0";
        AttentionCountText = "0";
        RemoteCountText = "0";
        Subtitle = "Reading the DevEcosystem folder…";
        LastRefreshText = "Not scanned yet";
        ProjectsPillText = "0 in DevEcosystem";
        ScanPathText = $"Scanning {_catalog.ScanRoot} · depth {ScanEngine.DefaultMaxDepth} · excludes DevDesk";
        BusyText = "Looking for projects…";

        // Progress<T> captures the context it is created on: created here (UI
        // thread), its callbacks land back on the UI thread, so the view model
        // never touches a bound property from a scan thread.
        _progress = new Progress<ScanProgress>(OnProgress);
    }

    public bool HasProjects => Projects.Count > 0;

    public bool IsEmpty => !HasProjects && !IsBusy && !HasError;

    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProjectsVisibility => HasProjects ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Page appearance: reuse the snapshot, scan only when there is none.</summary>
    [RelayCommand]
    private Task LoadAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async () =>
        {
            Apply(await _catalog.GetOrRefreshAsync(cancellationToken, _progress));
        });

    /// <summary>The Refresh button and the error bar's Retry button.</summary>
    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async () =>
        {
            Apply(await _catalog.RefreshAsync(cancellationToken, _progress));
        });

    private void OnProgress(ScanProgress progress)
    {
        BusyText = progress.Total <= 0
            ? $"Looking for projects… {progress.CurrentName}"
            : $"Scanning {progress.Completed} of {progress.Total} · {progress.CurrentName}";
    }

    private void Apply(CatalogSnapshot snapshot)
    {
        Projects = snapshot.Projects;
        ProjectCountText = snapshot.Projects.Count.ToString();
        CleanCountText = snapshot.CleanCount.ToString();
        AttentionCountText = snapshot.AttentionCount.ToString();
        RemoteCountText = snapshot.RemoteCount.ToString();
        ProjectsPillText = $"{snapshot.Projects.Count} in DevEcosystem";
        LastRefreshText = snapshot.CapturedAt == DateTimeOffset.MinValue
            ? "Not scanned yet"
            : $"Updated {snapshot.CapturedAt:HH:mm:ss} · {snapshot.Duration.TotalSeconds:0.0} s";

        Subtitle = snapshot.Projects.Count switch
        {
            0 => "No project folders found in the DevEcosystem folder.",
            _ when snapshot.AttentionCount == 0 => "All projects are healthy.",
            _ => $"{snapshot.AttentionCount} project(s) need attention.",
        };

        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(ProjectsVisibility));
    }

    protected override void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    protected override void OnErrorChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
    }
}
