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
/// State for the DevDesk page: the app's own checkout as a workspace, never a
/// scanned project. Reads the shared catalog, so opening this page after the
/// Dashboard does not rescan.
/// </summary>
public sealed partial class SelfViewModel : ViewModelBase
{
    private readonly ProjectCatalog _catalog;

    [ObservableProperty]
    public partial ProjectModel? Self { get; set; }

    [ObservableProperty]
    public partial string SelfNameText { get; set; }

    [ObservableProperty]
    public partial string SelfVersionText { get; set; }

    [ObservableProperty]
    public partial string SelfBranchText { get; set; }

    [ObservableProperty]
    public partial string SelfStatusText { get; set; }

    [ObservableProperty]
    public partial string SelfPathText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<CommitInfo> Commits { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ComponentInfo> Components { get; set; }

    public SelfViewModel(ProjectCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        SelfNameText = "DevDesk";
        SelfVersionText = string.Empty;
        SelfBranchText = string.Empty;
        SelfStatusText = "Reading the repo state…";
        SelfPathText = string.Empty;
        Commits = Array.Empty<CommitInfo>();
        Components = Array.Empty<ComponentInfo>();
    }

    public bool HasSelf => Self is not null;

    public Visibility ComponentsVisibility =>
        Components.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string SelfPath => Self?.Path ?? string.Empty;

    [RelayCommand]
    private Task LoadAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async () =>
        {
            Apply(await _catalog.GetOrRefreshAsync(cancellationToken));
        });

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async () =>
        {
            Apply(await _catalog.RefreshAsync(cancellationToken));
        });

    private void Apply(CatalogSnapshot snapshot)
    {
        Self = snapshot.Self;

        if (snapshot.Self is null)
        {
            SelfNameText = "DevDesk";
            SelfVersionText = string.Empty;
            SelfBranchText = string.Empty;
            SelfStatusText = "Self workspace not found — this build is installed, not a checkout.";
            SelfPathText = string.Empty;
            Commits = Array.Empty<CommitInfo>();
            Components = Array.Empty<ComponentInfo>();
        }
        else
        {
            SelfNameText = snapshot.Self.Name;
            SelfVersionText = snapshot.Self.VersionDisplay;
            SelfBranchText = snapshot.Self.Branch;
            SelfStatusText = snapshot.Self.ListSubtitle;
            SelfPathText = snapshot.Self.Path;
            Commits = snapshot.Self.RecentCommits;
            Components = snapshot.Self.Components;
        }

        OnPropertyChanged(nameof(HasSelf));
        OnPropertyChanged(nameof(ComponentsVisibility));
        OnPropertyChanged(nameof(SelfPath));
    }
}
