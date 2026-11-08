using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private readonly BuildHistory? _history;
    private readonly ProjectOrganizationStore? _organization;
    private IReadOnlyList<ProjectModel> _allProjects = Array.Empty<ProjectModel>();
    private string _filterQuery = string.Empty;
    private ProjectSortOrder _sortOrder = ProjectSortOrder.Name;
    private CatalogSnapshot _latestSnapshot = CatalogSnapshot.Empty;

    [ObservableProperty]
    public partial ObservableCollection<ProjectCardItem> Projects { get; set; }

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

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string EmptyTitle { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; }

    private readonly IProgress<ScanProgress> _progress;

    public DashboardViewModel(
        ProjectCatalog catalog,
        BuildHistory? history = null,
        ProjectOrganizationStore? organization = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _history = history;
        _organization = organization;

        Projects = new ObservableCollection<ProjectCardItem>();
        ProjectCountText = "0";
        CleanCountText = "0";
        AttentionCountText = "0";
        RemoteCountText = "0";
        Subtitle = "Reading configured scan roots…";
        LastRefreshText = "Not scanned yet";
        ProjectsPillText = "0 across enabled roots";
        ScanPathText = $"{SettingsService.Current.Settings.Roots.Count(root => root.Enabled)} enabled scan root(s) · excludes DevDesk";
        BusyText = "Looking for projects…";
        EmptyTitle = "No projects yet";
        EmptyMessage = "Add or enable a scan root in Settings, then refresh.";

        // Progress<T> captures the context it is created on: created here (UI
        // thread), its callbacks land back on the UI thread, so the view model
        // never touches a bound property from a scan thread.
        _progress = new Progress<ScanProgress>(OnProgress);
    }

    public bool HasProjects => Projects.Count > 0;

    public bool IsEmpty => !HasProjects && !IsBusy && !HasError;

    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProjectsVisibility => HasProjects ? Visibility.Visible : Visibility.Collapsed;

    public new Visibility BusyVisibility => IsBusy && !HasProjects
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>Page appearance: reuse the snapshot, scan only when there is none.</summary>
    [RelayCommand]
    private Task LoadAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async () =>
        {
            IsScanning = true;
            try
            {
                if (_catalog.Snapshot.Freshness == CatalogFreshness.Cached)
                {
                    ApplySnapshot(_catalog.Snapshot);
                }

                ApplySnapshot(await _catalog.GetOrRefreshAsync(cancellationToken, _progress));
            }
            finally
            {
                IsScanning = false;
            }
        });

    /// <summary>The Refresh button and the error bar's Retry button.</summary>
    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async () =>
        {
            IsScanning = true;
            try
            {
                ApplySnapshot(await _catalog.RefreshAsync(cancellationToken, _progress));
            }
            finally
            {
                IsScanning = false;
            }
        });

    [RelayCommand]
    private void CancelScan() => _catalog.CancelCurrentScan();

    private void OnProgress(ScanProgress progress)
    {
        var root = string.IsNullOrWhiteSpace(progress.RootPath)
            ? string.Empty
            : $" · {progress.RootPath}";
        BusyText = progress.Total <= 0
            ? $"Looking for projects… {progress.CurrentName}{root}"
            : $"Scanning {progress.Completed} of {progress.Total} · {progress.CurrentName}{root}";
    }

    public void ApplySnapshot(CatalogSnapshot snapshot)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using (PerfCounters.Current.BeginUiOperation("dashboard apply snapshot"))
        {
            IsScanning = _catalog.IsRefreshing;
            _latestSnapshot = snapshot;
            _allProjects = snapshot.Projects;
            UpdateProjectCards();
            ProjectCountText = snapshot.Projects.Count.ToString();
            CleanCountText = snapshot.PendingDetailsCount > 0 ? "—" : snapshot.CleanCount.ToString();
            AttentionCountText = snapshot.PendingDetailsCount > 0 ? "—" : snapshot.AttentionCount.ToString();
            RemoteCountText = snapshot.PendingDetailsCount > 0 ? "—" : snapshot.RemoteCount.ToString();
            UpdateProjectsPillText();
            LastRefreshText = snapshot.Freshness == CatalogFreshness.Partial && _catalog.IsRefreshing
                ? $"Scanning · {_allProjects.Count} real project(s) found so far"
                : snapshot.CapturedAt == DateTimeOffset.MinValue
                    ? "Not scanned yet"
                    : snapshot.Freshness switch
                {
                    CatalogFreshness.Cached => $"Cached {snapshot.CapturedAt:HH:mm:ss} · refreshing",
                    CatalogFreshness.Partial when _catalog.IsRefreshing =>
                        $"Scanning · {_allProjects.Count} project(s), {snapshot.PendingDetailsCount} details pending",
                    CatalogFreshness.Partial =>
                        $"Partial scan · {snapshot.RootDiagnostics.Count} root/folder warning(s)",
                    CatalogFreshness.Stale => $"Stale since {snapshot.CapturedAt:HH:mm:ss} · refresh failed",
                    _ => $"Updated {snapshot.CapturedAt:HH:mm:ss} · {snapshot.Duration.TotalSeconds:0.0} s",
                };

            Subtitle = snapshot.Freshness == CatalogFreshness.Partial && _catalog.IsRefreshing
                ? $"Scanning local roots · {_allProjects.Count} project(s) found so far."
                : snapshot.Freshness == CatalogFreshness.Partial
                    ? $"{snapshot.RootDiagnostics.Count} root/folder warning(s); readable roots remain available."
                    : _allProjects.Count switch
                {
                    0 => "No project folders found in the enabled scan roots.",
                    _ when snapshot.AttentionCount == 0 => "All projects are healthy.",
                    _ => $"{snapshot.AttentionCount} project(s) need attention.",
                };

            OnPropertyChanged(nameof(HasProjects));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyVisibility));
            OnPropertyChanged(nameof(ProjectsVisibility));
            OnPropertyChanged(nameof(BusyVisibility));
            UpdateEmptyState();
        }

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsedMs > 10)
            LoggingService.Log.Warning("Dashboard ApplySnapshot took {ElapsedMs:F0} ms for {Count} projects (scan={IsRefreshing})", elapsedMs, snapshot.Projects.Count, _catalog.IsRefreshing);
    }

    public void UpdateQuery(string? query, ProjectSortOrder sortOrder)
    {
        _filterQuery = query ?? string.Empty;
        _sortOrder = sortOrder;
        UpdateProjectCards();
        UpdateProjectsPillText();
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(ProjectsVisibility));
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (_allProjects.Count == 0 && _latestSnapshot.RootDiagnostics.Count > 0)
        {
            EmptyTitle = "Some scan roots are unavailable";
            var issue = _latestSnapshot.RootDiagnostics[0];
            var location = string.IsNullOrWhiteSpace(issue.Location) ? issue.RootPath : Path.Combine(issue.RootPath, issue.Location);
            EmptyMessage = $"{location}: {issue.Message} Other configured roots were still scanned.";
            return;
        }

        EmptyTitle = _allProjects.Count > 0 ? "No matching projects" : "No projects yet";
        EmptyMessage = _allProjects.Count > 0
            ? "Try a different search or clear the filter."
            : "Add or enable a scan root in Settings, then refresh.";
    }

    private void UpdateProjectsPillText() =>
        ProjectsPillText = Projects.Count == _allProjects.Count
            ? $"{_allProjects.Count} across enabled roots"
            : $"{Projects.Count} of {_allProjects.Count} projects";

    private void UpdateProjectCards()
    {
        var existingByPath = new Dictionary<string, ProjectCardItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in Projects)
        {
            existingByPath[card.Model.Path] = card;
        }

        var desired = ProjectListQuery.Apply(
                _allProjects,
                _filterQuery,
                _sortOrder,
                _organization?.Snapshot,
                SettingsService.Current.Settings.Roots,
                _organization?.HideArchived == true)
            .Select(project =>
            {
                var lastBuild = _history?.LastFor(project.Path);
                var organization = _organization?.Get(project.Path);
                if (existingByPath.TryGetValue(project.Path, out var existing)
                    && ReferenceEquals(existing.Model, project)
                    && ReferenceEquals(existing.LastBuild, lastBuild)
                    && OrganizationEquals(existing.Organization, organization))
                {
                    return existing;
                }

                return new ProjectCardItem(project, lastBuild, organization);
            })
            .ToArray();

        if (desired.Length > 0 && Projects.Count == 0)
        {
            Projects = new ObservableCollection<ProjectCardItem>(desired);
            return;
        }

        for (var index = 0; index < desired.Length; index++)
        {
            var item = desired[index];
            if (index < Projects.Count)
            {
                if (ReferenceEquals(Projects[index], item))
                {
                    continue;
                }

                if (string.Equals(
                    Projects[index].Model.Path,
                    item.Model.Path,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Projects[index] = item;
                    continue;
                }
            }

            var existingIndex = -1;
            for (var searchIndex = index + 1; searchIndex < Projects.Count; searchIndex++)
            {
                if (ReferenceEquals(Projects[searchIndex], item))
                {
                    existingIndex = searchIndex;
                    break;
                }

                if (string.Equals(
                    Projects[searchIndex].Model.Path,
                    item.Model.Path,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Projects[searchIndex] = item;
                    existingIndex = searchIndex;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                Projects.Move(existingIndex, index);
            }
            else
            {
                Projects.Insert(index, item);
            }
        }

        while (Projects.Count > desired.Length)
        {
            Projects.RemoveAt(Projects.Count - 1);
        }
    }

    private static bool OrganizationEquals(
        ProjectOrganizationEntry? left,
        ProjectOrganizationEntry? right) =>
        ReferenceEquals(left, right)
        || left is not null
        && right is not null
        && left.IsPinned == right.IsPinned
        && left.IsArchived == right.IsArchived
        && left.TagColor == right.TagColor
        && left.LastOpened == right.LastOpened
        && left.Tags.SequenceEqual(right.Tags, StringComparer.OrdinalIgnoreCase);

    protected override void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(BusyVisibility));
    }

    protected override void OnErrorChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
    }
}
