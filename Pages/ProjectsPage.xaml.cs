using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using DevDesk.Models;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class ProjectsPage : Page
{
    private readonly GitService _git = new();
    private readonly BuildService _builds = AppServices.Builds;
    private CancellationTokenSource? _detailsCancellation;
    private CancellationTokenSource? _targetCancellation;
    private CancellationTokenSource? _operationCancellation;
    private bool _operationRunning;
    private string? _conflictOperation;
    private bool _conflictsResolved;
    private int _historyPage;
    private ObservableCollection<CommitInfo> _commits = new();
    private GitRepositoryStatus? _latestStatus;
    private IReadOnlyList<CommitInfo>? _latestHistory;
    private IReadOnlyList<GitRemoteInfo>? _latestRemotes;
    private BuildProfileResolution? _profileResolution;
    private RunResolution? _runResolution;
    private BuildSession? _buildSession;
    private string? _lastLogPath;
    private string? _outputPath;
    private string? _profileStatusText;
    private IReadOnlyList<ProjectModel> _allProjects = Array.Empty<ProjectModel>();
    private bool _hasRootDiagnostics;
    private bool _initializingProjectList = true;
    private bool _catalogSubscribed;
    private CatalogSnapshot? _pendingCatalogSnapshot;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _catalogUpdateTimer;

    public ProjectsPage()
    {
        using (PerfCounters.Current.BeginUiOperation("projects page load"))
        {
            using (PerfCounters.Current.BeginUiOperation("projects xaml initialize"))
            {
                this.InitializeComponent();
            }

            _catalogUpdateTimer = DispatcherQueue.CreateTimer();
            _catalogUpdateTimer.Interval = TimeSpan.FromMilliseconds(50);
            _catalogUpdateTimer.IsRepeating = true;
            _catalogUpdateTimer.Tick += CatalogUpdateTimer_Tick;

            using (PerfCounters.Current.BeginUiOperation("projects saved views refresh"))
            {
                RefreshSavedViews();
                HideArchivedCheckBox.IsChecked = AppServices.Organization.HideArchived;
            }

            _initializingProjectList = false;
            Loaded += ProjectsPage_Loaded;
            Unloaded += ProjectsPage_Unloaded;
        }

        // Card container creation is the expensive half of the first frame (a
        // cached snapshot of six projects costs tens of milliseconds on its own).
        // Running it in the next dispatcher pass keeps page construction a single
        // short stretch of UI-thread work instead of stacking the two (M6).
        if (!DispatcherQueue.TryEnqueue(LoadProjectList))
        {
            LoadProjectList();
        }
    }

    private void LoadProjectList()
    {
        var snapshot = AppServices.Catalog.Snapshot;
        if (snapshot.Freshness == CatalogFreshness.Cached)
        {
            using (PerfCounters.Current.BeginUiOperation("projects cached list apply"))
            {
                ApplyProjectList(snapshot);
            }
        }

        _ = LoadProjectsAsync();
    }

    /// <summary>The project the detail pane is showing, for the open actions.</summary>
    private ProjectModel? Selected => ProjectsList.SelectedItem as ProjectModel;

    private async Task LoadProjectsAsync()
    {
        try
        {
            var snapshot = await AppServices.Catalog.GetOrRefreshAsync();
            ApplyProjectList(snapshot);
        }
        catch (Exception ex)
        {
            ShowError("Could not read the project list", ex.Message);
        }
    }

    private void ProjectsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_catalogSubscribed)
        {
            AppServices.Catalog.Changed += Catalog_Changed;
            AppServices.Organization.Changed += Organization_Changed;
            _catalogSubscribed = true;
        }

        _catalogUpdateTimer.Start();
        if (WorkspaceTargetsToggle.IsChecked == true && Selected is { } project)
        {
            _ = LoadWorkspaceTargetsAsync(project);
        }
    }

    private void ProjectsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _catalogUpdateTimer.Stop();
        _targetCancellation?.Cancel();
        if (_catalogSubscribed)
        {
            AppServices.Catalog.Changed -= Catalog_Changed;
            AppServices.Organization.Changed -= Organization_Changed;
            _catalogSubscribed = false;
        }
    }

    private void Catalog_Changed(object? sender, CatalogSnapshot snapshot) =>
        Interlocked.Exchange(ref _pendingCatalogSnapshot, snapshot);

    private void CatalogUpdateTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        var snapshot = Interlocked.Exchange(ref _pendingCatalogSnapshot, null);
        if (snapshot is not null && _catalogSubscribed)
        {
            ApplyProjectList(snapshot);
        }
    }

    private void Organization_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshSavedViews();
            RefreshProjectProjection();
        });

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializingProjectList)
        {
            RefreshProjectProjection();
        }
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializingProjectList)
        {
            RefreshProjectProjection();
        }
    }

    private void ApplyProjectList(CatalogSnapshot snapshot)
    {
        _allProjects = snapshot.Projects;
        _hasRootDiagnostics = snapshot.RootDiagnostics.Count > 0;
        ProjectCatalogStateText.Text = snapshot.Freshness switch
        {
            CatalogFreshness.Cached => $"Cached local snapshot from {snapshot.CapturedAt:HH:mm:ss}; refreshing in the background.",
            CatalogFreshness.Partial when AppServices.Catalog.IsRefreshing =>
                $"Scan in progress; showing {_allProjects.Count} project(s) found so far.",
            CatalogFreshness.Partial => snapshot.RootDiagnostics.Count == 0
                ? "Scan is partial."
                : $"Partial scan · {snapshot.RootDiagnostics.Count} warning(s): {Path.Combine(snapshot.RootDiagnostics[0].RootPath, snapshot.RootDiagnostics[0].Location)} — {snapshot.RootDiagnostics[0].Message}",
            CatalogFreshness.Stale => $"Showing stale local data from {snapshot.CapturedAt:HH:mm:ss}; the latest refresh failed.",
            _ => $"Local snapshot updated {snapshot.CapturedAt:HH:mm:ss}.",
        };
        RefreshProjectProjection();
    }

    private void RefreshProjectProjection()
    {
        var selectedPath = Selected?.Path;
        var filtered = ProjectListQuery.Apply(
            _allProjects,
            FilterBox.Text,
            SelectedSort,
            AppServices.Organization.Snapshot,
            SettingsService.Current.Settings.Roots,
            AppServices.Organization.HideArchived);
        ProjectsList.ItemsSource = filtered;

        if (filtered.Count == 0)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            ProjectNoMatchesText.Visibility = Visibility.Visible;
            ProjectNoMatchesText.Text = _allProjects.Count == 0 && _hasRootDiagnostics
                ? "No cards are available from the inaccessible roots. Check the scan status above."
                : _allProjects.Count == 0
                ? "No projects were found in the enabled scan roots."
                : "No projects match this search.";
            return;
        }

        ProjectNoMatchesText.Visibility = Visibility.Collapsed;
        DetailsPane.Visibility = Visibility.Visible;
        var selectedIndex = selectedPath is null
            ? -1
            : filtered.ToList().FindIndex(project =>
                string.Equals(project.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        ProjectsList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
    }

    private ProjectSortOrder SelectedSort => SortBox.SelectedIndex switch
    {
        1 => ProjectSortOrder.Activity,
        2 => ProjectSortOrder.RecentlyOpened,
        3 => ProjectSortOrder.Health,
        4 => ProjectSortOrder.Version,
        _ => ProjectSortOrder.Name,
    };

    private void RefreshSavedViews(string? selectName = null)
    {
        var oldInitializing = _initializingProjectList;
        _initializingProjectList = true;
        var views = AppServices.Organization.SavedViews;
        SavedViewsBox.ItemsSource = views;
        SavedViewsBox.SelectedItem = views.FirstOrDefault(view =>
            string.Equals(view.Name, selectName, StringComparison.OrdinalIgnoreCase));
        _initializingProjectList = oldInitializing;
    }

    private void SavedViewsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingProjectList || SavedViewsBox.SelectedItem is not SavedProjectView view)
        {
            return;
        }

        _initializingProjectList = true;
        FilterBox.Text = view.Query;
        SortBox.SelectedIndex = view.SortOrder switch
        {
            ProjectSortOrder.Activity => 1,
            ProjectSortOrder.RecentlyOpened => 2,
            ProjectSortOrder.Health => 3,
            ProjectSortOrder.Version => 4,
            _ => 0,
        };
        _initializingProjectList = false;
        RefreshProjectProjection();
    }

    private async void SaveProjectViewButton_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            Header = "View name",
            PlaceholderText = "For example: Needs attention",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, "Saved view name");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save project view",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var view = new SavedProjectView(nameBox.Text, FilterBox.Text, SelectedSort);
        if (!AppServices.Organization.SaveView(view))
        {
            ShowError("Could not save project view", "Choose a name and check that local organization data can be written.");
            return;
        }

        RefreshSavedViews(view.Name);
    }

    private void RemoveProjectViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (SavedViewsBox.SelectedItem is not SavedProjectView view)
        {
            return;
        }

        if (!AppServices.Organization.RemoveView(view.Name))
        {
            ShowError("Could not remove project view", "Check that local organization data can be written.");
            return;
        }

        RefreshSavedViews();
    }

    private void HideArchivedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializingProjectList
            && !AppServices.Organization.SetHideArchived(HideArchivedCheckBox.IsChecked == true))
        {
            ShowError("Could not save archive preference", "Check that local organization data can be written.");
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
        var organization = AppServices.Organization.Get(p.Path);
        PinProjectButton.Content = organization.IsPinned ? "Unpin" : "Pin";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PinProjectButton,
            organization.IsPinned ? "Unpin project" : "Pin project");
        ArchiveProjectButton.Content = organization.IsArchived ? "Unarchive" : "Archive";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ArchiveProjectButton,
            organization.IsArchived ? "Unarchive project" : "Archive project");
        ProjectTagsBox.Text = string.Join(", ", organization.Tags);
        ProjectTagColorBox.SelectedIndex = ProjectOrganizationStore.TagColors
            .ToList()
            .FindIndex(color => string.Equals(color, organization.TagColor, StringComparison.OrdinalIgnoreCase));
        ProjectOrganizationStatusText.Text = organization.LastOpened is { } opened
            ? $"Pinned projects appear first · Last opened {opened.ToLocalTime():g}"
            : "Pinned projects appear first · Open actions update recently-opened ordering.";

        OverviewBranch.Text = p.Branch;
        OverviewVersion.Text = p.VersionDisplay;

        // "No remote" is a fact about this folder, not a promise about a future
        // version: a workspace like this one has no remote and works fine.
        OverviewRepo.Text = p.DetailsPending
            ? "Reading local Git details…"
            : p.HasRemote
                ? $"{p.RemoteHost} — remote configured"
                : "No remote — everything here is local";

        OverviewTree.Text = p.DetailsPending
            ? "Reading working-tree state…"
            : p.Dirty ? "dirty — uncommitted changes present" : "clean";
        OverviewCommit.Text = p.RecentCommits.FirstOrDefault()?.Subject ?? "—";
        ApplyBuildBadge(p);
        ApplyBuildProfile(p);
        _ = LoadGitDetailsAsync(p);
        _targetCancellation?.Cancel();
        WorkspaceTargetsList.ItemsSource = null;
        WorkspaceTargetsStatusText.Text = "Expand to discover workspace targets.";
        if (WorkspaceTargetsToggle.IsChecked == true)
        {
            _ = LoadWorkspaceTargetsAsync(p);
        }
    }

    private void WorkspaceTargetsToggle_Checked(object sender, RoutedEventArgs e)
    {
        WorkspaceTargetsPanel.Visibility = Visibility.Visible;
        if (Selected is { } project)
        {
            _ = LoadWorkspaceTargetsAsync(project);
        }
    }

    private void WorkspaceTargetsToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _targetCancellation?.Cancel();
        WorkspaceTargetsPanel.Visibility = Visibility.Collapsed;
    }

    private async Task LoadWorkspaceTargetsAsync(ProjectModel project)
    {
        _targetCancellation?.Cancel();
        _targetCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _targetCancellation = cancellation;
        WorkspaceTargetsList.ItemsSource = null;
        WorkspaceTargetsStatusText.Text = "Discovering solution and project files…";

        try
        {
            var targets = await Task.Run(
                () => WorkspaceTargetDiscovery.Discover(project.Path),
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || !string.Equals(Selected?.Path, project.Path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            WorkspaceTargetsList.ItemsSource = targets;
            WorkspaceTargetsStatusText.Text = targets.Count == 0
                ? "No solution or project files were found within the supported workspace depth."
                : $"{targets.Count} target(s) found. Opening a target uses its registered Windows app.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError("Could not discover workspace targets", ex.Message);
            WorkspaceTargetsStatusText.Text = "Target discovery failed.";
        }
    }

    private void WorkspaceTargetOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || Selected is not { } project)
        {
            return;
        }

        if (!ProjectOpenActions.OpenWithDefaultApp(path))
        {
            ShowError("Could not open workspace target", path);
            return;
        }

        _ = AppServices.Organization.MarkOpened(project.Path);
    }

    private void PinProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } project)
        {
            return;
        }

        var current = AppServices.Organization.Get(project.Path);
        if (!AppServices.Organization.SetPinned(project.Path, !current.IsPinned))
        {
            ShowError("Could not save project pin", "Check that DevDesk can write its local project-organization file.");
            return;
        }

        PinProjectButton.Content = current.IsPinned ? "Pin" : "Unpin";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PinProjectButton,
            current.IsPinned ? "Pin project" : "Unpin project");
    }

    private void ArchiveProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } project)
        {
            return;
        }

        var current = AppServices.Organization.Get(project.Path);
        if (!AppServices.Organization.SetArchived(project.Path, !current.IsArchived))
        {
            ShowError("Could not save archive state", "Check that local project-organization data can be written.");
            return;
        }

        ArchiveProjectButton.Content = current.IsArchived ? "Archive" : "Unarchive";
    }

    private void SaveProjectTagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } project)
        {
            return;
        }

        var tags = ProjectTagsBox.Text.Split(
            new[] { ',', ';', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var color = ProjectTagColorBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? ProjectOrganizationStore.TagColors[0]
            : ProjectOrganizationStore.TagColors[0];
        if (!AppServices.Organization.SetTags(project.Path, tags, color))
        {
            ShowError("Could not save project tags", "Check that DevDesk can write its local project-organization file.");
            return;
        }

        ProjectOrganizationStatusText.Text = tags.Length == 0
            ? "Tags cleared. Pinned projects appear first."
            : $"{tags.Length} tag(s) saved. Pinned projects appear first.";
    }

    /// <summary>
    /// The header badge: the last remembered run for this project, read from
    /// <see cref="BuildHistory"/> and nowhere else (D-0.3.10).
    /// </summary>
    private void ApplyBuildBadge(ProjectModel project)
    {
        var record = AppServices.Activity.History.LastFor(project.Path);

        HeaderBuildBadge.Text = record?.BadgeText ?? "no build yet";
        HeaderBuildGlyph.Glyph = record?.Status switch
        {
            DevDesk.Models.OperationStatus.Succeeded => "\uE73E",
            DevDesk.Models.OperationStatus.Failed => "\uE783",
            DevDesk.Models.OperationStatus.Canceled => "\uE71A",
            _ => "\uE823",
        };

        var brushKey = record?.Status switch
        {
            DevDesk.Models.OperationStatus.Succeeded => "SystemFillColorSuccessBrush",
            DevDesk.Models.OperationStatus.Failed => "SystemFillColorCriticalBrush",
            _ => "TextFillColorTertiaryBrush",
        };

        var brush = (Brush)Application.Current.Resources[brushKey];
        HeaderBuildBadge.Foreground = brush;
        HeaderBuildGlyph.Foreground = brush;
    }

    /// <summary>
    /// Resolves what Build would run for this project and states it in visible
    /// text. A folder with no marker disables the buttons *and* says why: a
    /// tooltip on a disabled control is never read (AGENTS gotcha 12).
    /// </summary>
    private void ApplyBuildProfile(ProjectModel project)
    {
        var resolution = BuildService.ResolveProfile(project, AppServices.Settings.Settings);
        _profileResolution = resolution;
        _runResolution = resolution.Profile is { } resolved
            ? BuildOutputLocator.ResolveRunTarget(resolved)
            : null;

        if (resolution.Profile is not { } profile)
        {
            BuildButton.IsEnabled = false;
            RunButton.IsEnabled = false;
            PublishButton.IsEnabled = false;
            ProfileButton.IsEnabled = false;
            _profileStatusText = resolution.Reason ?? "Nothing to build here.";
            BuildStatusLine.Text = _profileStatusText;
            return;
        }

        BuildButton.IsEnabled = true;
        PublishButton.IsEnabled = true;
        ProfileButton.IsEnabled = true;

        SelectCombo(ConfigurationBox, profile.Configuration);
        SelectCombo(PlatformBox, profile.Platform);
        NoRestoreBox.IsChecked = profile.NoRestore;
        PlatformBox.IsEnabled = profile.PlatformApplies;
        PlatformNote.Visibility = profile.PlatformApplies ? Visibility.Collapsed : Visibility.Visible;
        PlatformNote.Text = "This project does not use a platform, so -p:Platform is not passed.";
        UpdateProfilePreview(profile);

        RunButton.IsEnabled = _runResolution?.CanRun == true;
        var runNote = _runResolution?.CanRun == true
            ? string.Empty
            : $" · Run unavailable: {_runResolution?.Reason ?? "no application project was found"}";

        BuildStatusLine.Text = (resolution.Warning ?? $"{profile.Summary} · {profile.TargetName}") + runNote;
        _profileStatusText = BuildStatusLine.Text;
    }

    /// <summary>The profile as the flyout currently shows it, which is what a click runs.</summary>
    private BuildProfile? CurrentProfile()
    {
        if (_profileResolution?.Profile is not { } profile)
        {
            return null;
        }

        return profile with
        {
            Configuration = SelectedText(ConfigurationBox) ?? profile.Configuration,
            Platform = BuildProfileResolver.NormalizePlatform(SelectedText(PlatformBox)),
            NoRestore = NoRestoreBox.IsChecked == true,
        };
    }

    private void ProfileField_Changed(object sender, SelectionChangedEventArgs e) => RefreshProfilePreview();

    private void ProfileField_Changed(object sender, RoutedEventArgs e) => RefreshProfilePreview();

    private void RefreshProfilePreview()
    {
        if (_profileResolution?.Profile is not { } profile)
        {
            return;
        }

        PlatformNote.Visibility = profile.PlatformApplies ? Visibility.Collapsed : Visibility.Visible;
        UpdateProfilePreview(CurrentProfile() ?? profile);
    }

    private void UpdateProfilePreview(BuildProfile profile)
    {
        ProfileCommand.Text = BuildCommandBuilder.Preview(BuildCommandBuilder.BuildArguments(profile));
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        var profile = CurrentProfile();
        if (project is null || profile is null)
        {
            return;
        }

        var key = NormalizePath(project.Path);
        AppServices.Settings.Settings.BuildProfiles.TryGetValue(key, out var existing);

        AppServices.Settings.Settings.BuildProfiles[key] = new BuildProfileSettings
        {
            Configuration = profile.Configuration,
            Platform = profile.Platform,
            NoRestore = profile.NoRestore,
            Target = existing?.Target,
            ExtraArgs = existing?.ExtraArgs ?? string.Empty,
        };
        AppServices.Settings.Save();

        BuildStatusLine.Text = $"Saved build profile for {project.Name}: {profile.Summary}.";
        ApplyBuildProfile(project);
    }

    private static string? SelectedText(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Content as string;

    private static void SelectCombo(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content as string, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return path.Trim();
        }
    }

    private async Task LoadGitDetailsAsync(ProjectModel project)
    {
        _detailsCancellation?.Cancel();
        _detailsCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _detailsCancellation = cancellation;
        _latestStatus = null;
        _latestHistory = null;
        _latestRemotes = null;

        ChangesList.ItemsSource = null;
        CommitsList.ItemsSource = null;
        BranchesList.ItemsSource = null;
        TagsList.ItemsSource = null;
        ChangesEmpty.Visibility = Visibility.Collapsed;
        CommitsEmpty.Visibility = Visibility.Collapsed;
        BranchesEmpty.Visibility = Visibility.Collapsed;
        _historyPage = 0;
        LoadOlderCommitsButton.Visibility = Visibility.Collapsed;
        CommitDiffstat.Text = string.Empty;
        FileDiffPreview.Text = string.Empty;
        PageInfoBar.IsOpen = false;
        ConflictPanel.Visibility = Visibility.Collapsed;
        OperationStatus.Visibility = Visibility.Visible;
        OperationMessage.Text = "Reading Git details…";
        SetOperationButtonsEnabled(false);

        if (project.StatusText == "Not a git repository")
        {
            ChangesEmpty.Text = "Git changes are unavailable for this folder.";
            CommitsEmpty.Text = "Git history is unavailable for this folder.";
            BranchesEmpty.Text = "Git branches are unavailable for this folder.";
            TagsEmpty.Text = "Git tags are unavailable for this folder.";
            ChangesEmpty.Visibility = Visibility.Visible;
            CommitsEmpty.Visibility = Visibility.Visible;
            BranchesEmpty.Visibility = Visibility.Visible;
            TagsEmpty.Visibility = Visibility.Visible;
            RemoteSummary.Text = "No Git repository.";
            TrackingSummary.Text = "Git tracking is unavailable for this folder.";
            OperationStatus.Visibility = Visibility.Collapsed;
            SetOperationButtonsEnabled(false);
            return;
        }

        try
        {
            var statusTask = _git.GetStatusAsync(project.Path, cancellation.Token);
            var historyTask = _git.GetHistoryAsync(project.Path, cancellationToken: cancellation.Token);
            var branchesTask = _git.GetBranchesAsync(project.Path, cancellation.Token);
            var conflictTask = _git.GetConflictStateAsync(project.Path, cancellation.Token);
            var remotesTask = _git.GetRemotesAsync(project.Path, cancellation.Token);
            var tagsTask = _git.GetTagsAsync(project.Path, cancellation.Token);
            await Task.WhenAll(statusTask, historyTask, branchesTask, conflictTask, remotesTask, tagsTask);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_detailsCancellation, cancellation))
                return;

            var status = await statusTask;
            var history = await historyTask;
            var branches = await branchesTask;
            var conflict = await conflictTask;
            var remotes = await remotesTask;
            var tags = await tagsTask;
            if (!status.Operation.Succeeded)
            {
                LoggingService.Log.Warning("Projects Git status for {Project} failed: {Error}", project.Name, status.Operation.Error);
                ShowError("Git details unavailable", status.Operation.Error);
                return;
            }

            var repositoryStatus = status.Value!;
            _latestStatus = repositoryStatus;
            _latestHistory = history.Value!;
            _latestRemotes = remotes.Value!;
            OverviewBranch.Text = repositoryStatus.Branch ?? "detached";
            OverviewTree.Text = repositoryStatus.IsBareRepository
                ? "bare repository — no working tree"
                : repositoryStatus.Files.Count > 0 ? "dirty — uncommitted changes present" : "clean";
            ChangesEmpty.Text = repositoryStatus.IsBareRepository ? "This bare repository has no working tree." : "Working tree is clean.";
            SetOperationButtonsEnabled(!_operationRunning);

            var files = repositoryStatus.Files;
            ChangesList.ItemsSource = files;
            ChangesEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _commits = new ObservableCollection<CommitInfo>(history.Value!);
            CommitsList.ItemsSource = _commits;
            CommitsEmpty.Visibility = history.Value!.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadOlderCommitsButton.Visibility = history.Value.Count == 50 ? Visibility.Visible : Visibility.Collapsed;
            BranchesList.ItemsSource = branches.Value;
            BranchesEmpty.Visibility = branches.Value!.All(branch => branch.IsRemote) ? Visibility.Visible : Visibility.Collapsed;
            TagsList.ItemsSource = tags.Value;
            TagsEmpty.Visibility = tags.Value!.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RemoteSummary.Text = remotes.Value!.Count == 0
                ? "No remotes configured."
                : "Remotes: " + string.Join(" · ", remotes.Value.Select(remote => $"{remote.Name} ({remote.Direction}) {remote.Url}"));
            TrackingSummary.Text = repositoryStatus.Upstream is null
                ? $"{repositoryStatus.Branch ?? "Detached HEAD"} · no upstream · local branch"
                : $"{repositoryStatus.Branch} → {repositoryStatus.Upstream} · ahead {repositoryStatus.Ahead}, behind {repositoryStatus.Behind}";

            _conflictOperation = conflict.Value?.Operation;
            var conflictedFiles = files.Where(file => file.Kind == "conflict").ToArray();
            _conflictsResolved = conflictedFiles.Length == 0;
            ConflictFiles.ItemsSource = conflictedFiles;
            ConflictPanel.Visibility = conflict.Value?.HasConflict == true ? Visibility.Visible : Visibility.Collapsed;
            ConflictSummary.Text = $"{_conflictOperation ?? "Git"} operation needs attention · {conflictedFiles.Length} conflicted file(s)";
            ContinueConflictButton.IsEnabled = _conflictOperation is not null && _conflictsResolved;

            var secondaryFailure = !history.Operation.Succeeded ? history.Operation
                : !branches.Operation.Succeeded ? branches.Operation
                : !remotes.Operation.Succeeded ? remotes.Operation
                : !tags.Operation.Succeeded ? tags.Operation
                : null;
            if (secondaryFailure is not null)
                ShowError("Some Git details could not be loaded", secondaryFailure.Error);
        }
        catch (OperationCanceledException)
        {
            // A project selection changed while the previous read was in flight.
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
                ShowError("Git details could not be loaded", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_detailsCancellation, cancellation) && !_operationRunning)
                OperationStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OverviewPanel.Visibility = DetailTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesPanel.Visibility = DetailTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CommitsPanel.Visibility = DetailTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        BranchesPanel.Visibility = DetailTabs.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        BuildPanel.Visibility = DetailTabs.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BranchesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetOperationButtonsEnabled(!_operationRunning && _latestStatus is not null);

    private void TagsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetOperationButtonsEnabled(!_operationRunning && _latestStatus is not null);

    private async void ChangesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetOperationButtonsEnabled(!_operationRunning && _latestStatus is not null);
        var project = Selected;
        var file = SelectedChanges().FirstOrDefault();
        if (project is null || file is null)
        {
            FileDiffPreview.Text = string.Empty;
            return;
        }
        if (file.Kind == "untracked")
        {
            FileDiffPreview.Text = "Untracked files do not have a Git diff until staged.";
            return;
        }
        var request = _detailsCancellation;
        var result = await _git.GetFileDiffAsync(project.Path, file.Path, file.IndexStatus != ".",
            request?.Token ?? CancellationToken.None);
        if (ReferenceEquals(request, _detailsCancellation) && SelectedChanges().Any(item => item.Path == file.Path))
            FileDiffPreview.Text = result.Succeeded ? result.Output : result.Error;
    }

    private async void CommitsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } project || CommitsList.SelectedItem is not CommitInfo commit) return;
        var request = _detailsCancellation;
        var result = await _git.GetCommitDiffstatAsync(project.Path, commit.Hash, request?.Token ?? CancellationToken.None);
        if (ReferenceEquals(request, _detailsCancellation) && CommitsList.SelectedItem is CommitInfo selected && selected.Hash == commit.Hash)
            CommitDiffstat.Text = result.Succeeded ? result.Output : result.Error;
    }

    private async void LoadOlderCommitsButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null || _operationRunning) return;
        var nextPage = _historyPage + 1;
        OperationStatus.Visibility = Visibility.Visible;
        OperationMessage.Text = "Loading commit history…";
        try
        {
            var result = await _git.GetHistoryAsync(project.Path, nextPage, cancellationToken: _detailsCancellation?.Token ?? CancellationToken.None);
            if (!result.Operation.Succeeded) { ShowError("Could not load more history", result.Operation.Error); return; }
            foreach (var commit in result.Value!) _commits.Add(commit);
            _historyPage = nextPage;
            LoadOlderCommitsButton.Visibility = result.Value.Count == 50 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            OperationStatus.Visibility = Visibility.Collapsed;
        }
    }

    private async void StageSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths();
        if (paths.Length == 0) { ShowError("Select changes first", "Choose one or more files in the list."); return; }
        var project = Selected;
        if (project is null) return;
        await RunGitOperationAsync(project, "Stage selected files", "Add these paths to the Git index.",
            FormatCommand(new[] { "git", "add", "--" }.Concat(paths)),
            token => _git.StageAsync(project.Path, paths, token));
    }

    private async void StageAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } project) return;
        await RunGitOperationAsync(project, "Stage all changes", "Add all tracked and untracked changes to the Git index.",
            "git add --all", token => _git.StageAllAsync(project.Path, token));
    }

    private async void RefreshGitButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } project || _operationRunning) return;
        await LoadGitDetailsAsync(project);
        PublishProjectState(project);
    }

    private async void UnstageSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var files = SelectedChanges().Where(file => file.IndexStatus != "." && file.IndexStatus != "?").ToArray();
        if (files.Length == 0) { ShowError("No staged files selected", "Select at least one staged path."); return; }
        var project = Selected;
        if (project is null) return;
        var paths = files.Select(file => file.Path).ToArray();
        await RunGitOperationAsync(project, "Unstage selected files", "Remove these paths from the index while keeping their working tree content.",
            FormatCommand(new[] { "git", "restore", "--staged", "--" }.Concat(paths)),
            token => _git.UnstageAsync(project.Path, paths, token));
    }

    private async void DiscardSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var files = SelectedChanges().Where(file => file.Kind != "untracked").ToArray();
        if (files.Length == 0) { ShowError("No tracked changes selected", "Select tracked files to restore from HEAD."); return; }
        var project = Selected;
        if (project is null) return;
        var paths = files.Select(file => file.Path).ToArray();
        await RunGitOperationAsync(project, "Discard tracked changes", "Restore selected tracked files from HEAD. This cannot recover their current edits.",
            FormatCommand(new[] { "git", "restore", "--worktree", "--" }.Concat(paths)),
            token => _git.DiscardTrackedChangesAsync(project.Path, paths, token), project.Name);
    }

    private async void CommitButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;

        var message = new TextBox { PlaceholderText = "Describe the change", AcceptsReturn = true, MinHeight = 72, TextWrapping = TextWrapping.Wrap };
        var amend = new CheckBox { Content = "Amend previous commit" };
        var signOff = new CheckBox { Content = "Add sign-off" };
        var pushAfter = new CheckBox { Content = "Push after commit" };
        var guidance = new TextBlock { Text = "Keep the first line concise. Longer messages remain valid.", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        message.TextChanged += (_, _) => guidance.Text = message.Text.Length > 72
            ? $"First line is {message.Text.Split('\n')[0].Length} characters. Longer messages remain valid."
            : "Keep the first line concise. Longer messages remain valid.";
        amend.Checked += (_, _) => message.Text = project.RecentCommits.FirstOrDefault()?.Subject ?? string.Empty;
        amend.Unchecked += (_, _) => message.Text = string.Empty;
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(new TextBlock { Text = "Commit message", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        form.Children.Add(message);
        form.Children.Add(guidance);
        form.Children.Add(amend);
        form.Children.Add(signOff);
        form.Children.Add(pushAfter);

        var dialog = new ContentDialog
        {
            Title = $"Commit in {project.Name}",
            Content = form,
            PrimaryButtonText = "Review command",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                guidance.Text = "Enter a commit message to continue.";
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var args = new List<string> { "git", "commit" };
        if (amend.IsChecked == true) args.Add("--amend");
        if (signOff.IsChecked == true) args.Add("--signoff");
        args.Add("-m");
        args.Add(message.Text);
        var preview = FormatCommand(args);
        var activeBranch = BranchesList.Items.OfType<GitBranchInfo>().FirstOrDefault(item => item.IsCurrent);
        if (pushAfter.IsChecked == true)
        {
            if (_latestRemotes?.Count is not > 0)
            {
                ShowError("No remote configured", "Add an origin remote before enabling push after commit.");
                return;
            }
            var branchName = activeBranch?.Name ?? _latestStatus?.Branch ?? "<current-branch>";
            var pushArgs = activeBranch?.Upstream is null
                ? new[] { "git", "push", "--set-upstream", "origin", branchName }
                : new[] { "git", "push", "origin", branchName };
            preview += Environment.NewLine + FormatCommand(pushArgs);
        }
        await RunGitOperationAsync(project, "Create commit", "Commit the staged changes shown in the Changes list.", preview,
            async token =>
            {
                var commit = await _git.CommitAsync(project.Path, message.Text, amend.IsChecked == true, signOff.IsChecked == true, token);
                if (!commit.Succeeded || pushAfter.IsChecked != true) return commit;
                var push = await _git.PushAsync(project.Path, token);
                return push with
                {
                    Output = string.Join(Environment.NewLine, new[] { commit.Output, push.Output }.Where(text => !string.IsNullOrWhiteSpace(text))),
                    Error = push.Succeeded ? push.Error : "Commit was created, but push failed. " + push.Error,
                };
            });
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        await RunGitOperationAsync(project, "Fetch from origin", "Download remote refs. This does not change files or merge commits.",
            "git fetch origin", token => _git.FetchAsync(project.Path, token));
    }

    private async void PullButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        await RunGitOperationAsync(project, "Fast-forward pull", "Update the current branch only when it can fast-forward cleanly.",
            "git pull --ff-only", token => _git.PullFastForwardOnlyAsync(project.Path, token));
    }

    private async void PushButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        var branch = _latestStatus?.Branch;
        if (string.IsNullOrWhiteSpace(branch)) { ShowError("Cannot push while detached", "Switch to a named local branch before pushing."); return; }
        var hasUpstream = BranchesList.Items.OfType<GitBranchInfo>().FirstOrDefault(item => item.IsCurrent)?.Upstream is not null;
        var preview = hasUpstream ? FormatCommand(new[] { "git", "push", "origin", branch })
            : FormatCommand(new[] { "git", "push", "--set-upstream", "origin", branch });
        await RunGitOperationAsync(project, "Push current branch", "Upload commits to the configured origin.", preview,
            token => _git.PushAsync(project.Path, token));
    }

    private async void CreateBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        var name = await AskForTextAsync($"New branch in {project.Name}", "Branch name", "Create branch");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunGitOperationAsync(project, "Create and switch branch", "Create a new local branch from the current commit and switch to it.",
            FormatCommand(new[] { "git", "switch", "-c", name }), token => _git.CreateBranchAsync(project.Path, name, token));
    }

    private async void SwitchBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (BranchesList.SelectedItem is not GitBranchInfo branch || Selected is not { } project) return;
        if (branch.IsRemote) { ShowError("Remote branches are read only", "Create a local branch from the remote branch before switching."); return; }
        await RunGitOperationAsync(project, "Switch branch", "Change the checked out branch. Git will refuse if local changes would be overwritten.",
            FormatCommand(new[] { "git", "switch", branch.Name }), token => _git.SwitchBranchAsync(project.Path, branch.Name, token));
    }

    private async void DeleteBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (BranchesList.SelectedItem is not GitBranchInfo branch || Selected is not { } project) return;
        if (branch.IsRemote) { ShowError("Remote branches are read only", "Delete a local tracking branch instead."); return; }
        if (branch.IsCurrent) { ShowError("Cannot delete the current branch", "Switch to another branch first."); return; }
        await RunGitOperationAsync(project, "Delete merged branch", "Git deletes only branches already merged into the current branch.",
            FormatCommand(new[] { "git", "branch", "-d", branch.Name }), token => _git.DeleteMergedBranchAsync(project.Path, branch.Name, token), branch.Name);
    }

    private async void CreateTagButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        var name = await AskForTextAsync($"Create local tag in {project.Name}", "Tag name", "Continue");
        if (string.IsNullOrWhiteSpace(name)) return;
        var message = await AskForTextAsync("Annotated tag message", "Tag message", "Review tag");
        if (string.IsNullOrWhiteSpace(message)) return;
        await RunGitOperationAsync(project, "Create local tag", "Create an annotated local tag. This does not upload the tag.",
            FormatCommand(new[] { "git", "tag", "-a", name, "-m", message }),
            token => _git.CreateTagAsync(project.Path, name, message, token));
    }

    private async void DeleteTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagsList.SelectedItem is not GitTagInfo tag || Selected is not { } project) return;
        await RunGitOperationAsync(project, "Delete local tag", "Remove this tag from the local repository. No remote tag is changed.",
            FormatCommand(new[] { "git", "tag", "-d", tag.Name }), token => _git.DeleteLocalTagAsync(project.Path, tag.Name, token), tag.Name);
    }

    private async void AbortConflictButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null || _conflictOperation is null || !_conflictsResolved) return;
        var merge = _conflictOperation == "merge";
        var args = merge ? new[] { "git", "merge", "--abort" } : new[] { "git", "rebase", "--abort" };
        await RunGitOperationAsync(project, $"Abort {_conflictOperation}", "Discard the in-progress merge or rebase and return to its pre-operation state.",
            FormatCommand(args), token => merge ? _git.AbortMergeAsync(project.Path, token) : _git.AbortRebaseAsync(project.Path, token), project.Name);
    }

    private async void ContinueConflictButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null || _conflictOperation is null) return;
        var merge = _conflictOperation == "merge";
        var args = merge ? new[] { "git", "-c", "core.editor=true", "merge", "--continue" }
            : new[] { "git", "-c", "core.editor=true", "rebase", "--continue" };
        await RunGitOperationAsync(project, $"Continue {_conflictOperation}", "Continue using the resolved files. Git keeps its prepared operation message.",
            FormatCommand(args), token => merge ? _git.ContinueMergeAsync(project.Path, token) : _git.ContinueRebaseAsync(project.Path, token));
    }

    private async Task RunGitOperationAsync(ProjectModel project, string title, string consequence, string command,
        Func<CancellationToken, Task<GitOperationResult>> operation, string? typedConfirmation = null)
    {
        if (_operationRunning) return;
        if (!await ConfirmOperationAsync(title, consequence, command, typedConfirmation)) return;

        _operationRunning = true;
        _operationCancellation = new CancellationTokenSource();
        ProjectsList.IsEnabled = false;
        LoggingService.Log.Information("Git operation {Operation} started for {Project}", title, project.Name);
        OperationStatus.Visibility = Visibility.Visible;
        OperationMessage.Text = title + "…";
        CancelOperationButton.IsEnabled = true;
        SetOperationButtonsEnabled(false);
        try
        {
            var result = await operation(_operationCancellation.Token);
            if (!result.Succeeded)
            {
                LoggingService.Log.Warning("Git operation {Operation} failed for {Project}: {Error}", title, project.Name, result.Error);
                await LoadGitDetailsAsync(project);
                PublishProjectState(project);
                if (result.Failure == GitFailureKind.Cancelled)
                {
                    PageInfoBar.Severity = InfoBarSeverity.Warning;
                    PageInfoBar.Title = title + " cancelled";
                    PageInfoBar.Message = "Git state was refreshed. A cancelled network transfer may have completed partially.";
                    PageInfoBar.IsOpen = true;
                    return;
                }
                ShowError(title + " failed", result.Error);
                return;
            }

            await LoadGitDetailsAsync(project);
            PublishProjectState(project);
            LoggingService.Log.Information("Git operation {Operation} completed for {Project}", title, project.Name);
            PageInfoBar.Severity = InfoBarSeverity.Success;
            PageInfoBar.Title = title;
            var output = result.Output.Trim();
            PageInfoBar.Message = output.Length == 0 ? "Completed successfully." : output.Length > 1000 ? output[..1000] + "…" : output;
            PageInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Git operation {Operation} failed for {Project}", title, project.Name);
            ShowError(title + " failed", GitService.Redact(ex.Message));
        }
        finally
        {
            _operationRunning = false;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            ProjectsList.IsEnabled = true;
            OperationStatus.Visibility = Visibility.Collapsed;
            CancelOperationButton.IsEnabled = false;
            SetOperationButtonsEnabled(true);
        }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    // ---------------------------------------------------------------------
    // Build / Run / Publish (0.3.0). The command is always built as an
    // argument list by BuildCommandBuilder; this page only decides which verb
    // the user asked for and reports what happened.
    // ---------------------------------------------------------------------

    private async void BuildButton_Click(object sender, RoutedEventArgs e) =>
        await StartBuildAsync(OperationKind.Build);

    private async void RunButton_Click(object sender, RoutedEventArgs e) =>
        await StartBuildAsync(OperationKind.Run);

    private async void PublishButton_Click(object sender, RoutedEventArgs e) =>
        await StartBuildAsync(OperationKind.Publish);

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buildSession is { } session)
        {
            AppServices.Activity.Cancel(session.OperationId);
        }
    }

    private async Task StartBuildAsync(OperationKind kind)
    {
        var project = Selected;
        var profile = CurrentProfile();

        if (project is null || profile is null)
        {
            ShowError("Nothing to build", _profileResolution?.Reason ?? "Select a project first.");
            return;
        }

        if (_buildSession is { } active && !active.Completion.IsCompleted)
        {
            ShowWarning("A build is already running", "Wait for it to finish, or press Stop.");
            return;
        }

        if (kind == OperationKind.Publish
            && !await ConfirmOperationAsync(
                $"Publish {project.Name}",
                "Publish writes the app's own publish output. DevDesk never chooses an output folder, so the project file stays in charge.",
                BuildCommandBuilder.Preview(BuildCommandBuilder.PublishArguments(profile)),
                typedConfirmation: null))
        {
            return;
        }

        BuildOutput.Clear();
        _lastLogPath = null;
        _outputPath = null;
        OpenBuildLogButton.IsEnabled = false;
        OpenLogFolderButton.IsEnabled = false;
        OpenOutputButton.Visibility = Visibility.Collapsed;
        ProblemsList.ItemsSource = null;
        ProblemsEmpty.Visibility = Visibility.Collapsed;
        ProblemCount.Text = string.Empty;
        SetBuildRunning(true);
        BuildSummary.Text = $"{Verb(kind)} {project.Name} · {profile.Summary} · running…";
        BuildSummary.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        BuildHint.Text = "The console fills as the tool writes. The complete output is saved to the build log.";

        var session = kind switch
        {
            OperationKind.Publish => _builds.Publish(project, profile, BuildOutput),
            OperationKind.Run => _builds.Run(project, profile, BuildOutput),
            _ => _builds.Build(project, profile, BuildOutput),
        };

        _buildSession = session;

        try
        {
            var finished = await session.Completion;
            ApplyBuildOutcome(project, finished);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "{Kind} failed for {Project}", kind, project.Name);
            ShowError($"{Verb(kind)} failed", ex.Message);
        }
        finally
        {
            SetBuildRunning(false);
        }
    }

    private void ApplyBuildOutcome(ProjectModel project, Operation operation)
    {
        _lastLogPath = operation.LogPath;
        var canOpen = !string.IsNullOrWhiteSpace(_lastLogPath) && System.IO.File.Exists(_lastLogPath);
        OpenBuildLogButton.IsEnabled = canOpen;
        OpenLogFolderButton.IsEnabled = canOpen;

        // The publish folder is whatever MSBuild printed, not a path this app
        // constructed; it is only offered when that folder really exists.
        _outputPath = operation.OutputPath;
        OpenOutputButton.Visibility = !string.IsNullOrWhiteSpace(_outputPath)
            && System.IO.Directory.Exists(_outputPath)
                ? Visibility.Visible
                : Visibility.Collapsed;

        ApplyBuildBadge(project);

        BuildSummary.Text = operation.Status switch
        {
            DevDesk.Models.OperationStatus.Succeeded =>
                $"{operation.KindLabel} {project.Name} succeeded in {operation.DurationText} · exit {operation.ExitCode ?? 0}",
            DevDesk.Models.OperationStatus.Failed =>
                $"{operation.KindLabel} {project.Name} failed in {operation.DurationText}"
                + (operation.ExitCode is { } code ? $" · exit {code}" : string.Empty),
            DevDesk.Models.OperationStatus.Canceled =>
                $"{operation.KindLabel} {project.Name} cancelled after {operation.DurationText} · not a failure",
            _ => $"{operation.KindLabel} {project.Name} · {operation.StatusLabel}",
        };

        BuildSummary.Foreground = (Brush)Application.Current.Resources[operation.Status switch
        {
            DevDesk.Models.OperationStatus.Succeeded => "SystemFillColorSuccessBrush",
            DevDesk.Models.OperationStatus.Failed => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        }];

        BuildHint.Text = operation.FirstError
            ?? operation.Hint
            ?? (operation.Status == DevDesk.Models.OperationStatus.Succeeded
                ? "The full output is in the build log."
                : string.Empty);

        var problems = _buildSession?.Problems;
        ProblemsList.ItemsSource = problems?.Problems;        ProblemsEmpty.Visibility = (problems?.Problems.Count ?? 0) == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProblemsEmpty.Text = operation.Status == DevDesk.Models.OperationStatus.Succeeded
            ? "No problems reported. This build produced no errors or warnings."
            : "No problems were parsed from this output — open the log to read the raw text.";
        ProblemCount.Text = problems is null
            ? string.Empty
            : $"{problems.ErrorCount} error(s) · {problems.WarningCount} warning(s)"
              + (problems.Truncated ? " · showing the first 500" : string.Empty);
    }

    private void ProblemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ProblemsList.SelectedItem is BuildProblem problem)
        {
            ProblemNavigator.Open(problem);
        }
    }

    private void CopyProblem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string text } || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void SetBuildRunning(bool running)
    {
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        var idle = !running && _profileResolution?.IsBuildable == true;
        BuildButton.IsEnabled = idle;
        PublishButton.IsEnabled = idle;
        ProfileButton.IsEnabled = idle;
        RunButton.IsEnabled = idle && _runResolution?.CanRun == true;

        BuildStatusLine.Text = running
            ? "Building… the buttons return when it finishes."
            : _profileStatusText ?? string.Empty;
    }

    private void OpenBuildLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastLogPath) || !ProjectOpenActions.OpenWithDefaultApp(_lastLogPath))
        {
            ShowError("No build log yet", "Run a build first; the log is written next to the app's data.");
        }
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastLogPath) || !ProjectOpenActions.RevealInExplorer(_lastLogPath))
        {
            ShowError("No build log yet", "Run a build first; the log is written next to the app's data.");
        }
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_outputPath) || !ProjectOpenActions.OpenInExplorer(_outputPath))
        {
            ShowError(
                "No publish output yet",
                "Publish a project first; the folder comes from the path MSBuild reports, never from a guess.");
        }
    }

    private void ShowWarning(string title, string message)
    {
        PageInfoBar.Severity = InfoBarSeverity.Warning;
        PageInfoBar.Title = title;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }

    private static string Verb(OperationKind kind) => kind switch
    {
        OperationKind.Publish => "Publish",
        OperationKind.Run => "Run",
        _ => "Build",
    };


    private async Task<bool> ConfirmOperationAsync(string title, string consequence, string command, string? typedConfirmation)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = consequence, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Command preview", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = command, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap });
        TextBox? confirmation = null;
        if (typedConfirmation is not null)
        {
            content.Children.Add(new TextBlock { Text = $"Type {typedConfirmation} to confirm.", TextWrapping = TextWrapping.Wrap });
            confirmation = new TextBox { PlaceholderText = typedConfirmation };
            content.Children.Add(confirmation);
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "Run",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (confirmation is not null)
        {
            var typed = confirmation;
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = !string.Equals(typed.Text, typedConfirmation, StringComparison.Ordinal);
        }
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> AskForTextAsync(string title, string placeholder, string accept)
    {
        var input = new TextBox { PlaceholderText = placeholder };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = accept,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    private GitFileStatus[] SelectedChanges() => ChangesList.SelectedItems.OfType<GitFileStatus>().ToArray();
    private string[] SelectedPaths() => SelectedChanges().Select(file => file.Path).ToArray();

    private void SetOperationButtonsEnabled(bool enabled)
    {
        var canWrite = enabled && _latestStatus?.IsBareRepository != true;
        foreach (var button in new[] { StageAllButton, FetchButton, PullButton, PushButton, CreateBranchButton, CreateTagButton })
            button.IsEnabled = canWrite;
        var selectedFiles = SelectedChanges();
        StageSelectedButton.IsEnabled = canWrite && selectedFiles.Length > 0;
        UnstageSelectedButton.IsEnabled = canWrite && selectedFiles.Any(file => file.IndexStatus != "." && file.IndexStatus != "?");
        DiscardSelectedButton.IsEnabled = canWrite && selectedFiles.Any(file => file.Kind != "untracked");
        CommitButton.IsEnabled = canWrite && _latestStatus?.Files.Any(file => file.IndexStatus != "." && file.IndexStatus != "?") == true;
        RefreshGitButton.IsEnabled = enabled;
        var selectedBranch = BranchesList.SelectedItem as GitBranchInfo;
        SwitchBranchButton.IsEnabled = canWrite && selectedBranch is { IsRemote: false, IsCurrent: false };
        DeleteBranchButton.IsEnabled = canWrite && selectedBranch is { IsRemote: false, IsCurrent: false };
        DeleteTagButton.IsEnabled = canWrite && TagsList.SelectedItem is GitTagInfo;
        AbortConflictButton.IsEnabled = canWrite;
        ContinueConflictButton.IsEnabled = canWrite && _conflictOperation is not null && _conflictsResolved;

        // A build and a Git write must not overlap: switching branch under a
        // running compiler is the kind of race that produces a mystery failure.
        var buildIdle = _buildSession is null || _buildSession.Completion.IsCompleted;
        var buildable = enabled && buildIdle && _profileResolution?.IsBuildable == true;
        BuildButton.IsEnabled = buildable;
        PublishButton.IsEnabled = buildable;
        ProfileButton.IsEnabled = buildable;
        RunButton.IsEnabled = buildable && _runResolution?.CanRun == true;
    }

    private void PublishProjectState(ProjectModel project)
    {
        if (_latestStatus is null || _latestHistory is null || _latestRemotes is null) return;
        var dirty = _latestStatus.Files.Count > 0;
        var behind = _latestStatus.Behind > 0;
        var updated = new ProjectModel
        {
            Name = project.Name,
            Path = project.Path,
            IconGlyph = project.IconGlyph,
            IsWorkspace = project.IsWorkspace,
            Branch = _latestStatus.Branch ?? "detached",
            Version = project.Version,
            VersionMismatch = project.VersionMismatch,
            HasRemote = _latestRemotes.Count > 0,
            RemoteOk = project.RemoteOk,
            RemoteHost = project.RemoteHost,
            Dirty = dirty,
            BehindUpstream = behind,
            StatusText = !project.RemoteOk ? "Remote unavailable" : dirty ? "Changes" : behind ? "Behind upstream" : "Healthy",
            RecentCommits = _latestHistory.Take(3).ToArray(),
            Components = project.Components,
        };
        if (!AppServices.Catalog.UpdateProject(updated)) return;
        var index = ProjectsList.SelectedIndex;
        ProjectsList.ItemsSource = AppServices.Catalog.Snapshot.Projects;
        if (index >= 0 && index < ProjectsList.Items.Count) ProjectsList.SelectedIndex = index;
    }

    private static string FormatCommand(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
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
            return;
        }

        _ = AppServices.Organization.MarkOpened(project.Path);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null)
            return;

        if (!ProjectOpenActions.OpenInExplorer(project.Path))
        {
            ShowError("Could not open the folder", project.Path);
            return;
        }

        _ = AppServices.Organization.MarkOpened(project.Path);
    }

    private void ShowError(string title, string message)
    {
        PageInfoBar.Severity = InfoBarSeverity.Error;
        PageInfoBar.Title = title;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }
}
