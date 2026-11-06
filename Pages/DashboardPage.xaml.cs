using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Models;
using DevDesk.Services;
using DevDesk.ViewModels;

namespace DevDesk.Pages;

/// <summary>
/// Dashboard shell. All state comes from <see cref="DashboardViewModel"/>
/// (which reads the shared catalog); this file only wires the card menu, which
/// is view behaviour rather than state.
/// </summary>
public sealed partial class DashboardPage : Page
{
    private bool _catalogSubscribed;
    private bool _layoutSubscribed;
    private CatalogSnapshot? _pendingCatalogSnapshot;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _catalogUpdateTimer;

    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = new DashboardViewModel(
            AppServices.Catalog,
            AppServices.Activity.History,
            AppServices.Organization);
        this.InitializeComponent();
        _catalogUpdateTimer = DispatcherQueue.CreateTimer();
        _catalogUpdateTimer.Interval = System.TimeSpan.FromMilliseconds(50);
        _catalogUpdateTimer.IsRepeating = true;
        _catalogUpdateTimer.Tick += CatalogUpdateTimer_Tick;
        Loaded += DashboardPage_Loaded;
        Unloaded += DashboardPage_Unloaded;
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_catalogSubscribed)
        {
            AppServices.Catalog.Changed += Catalog_Changed;
            AppServices.Organization.Changed += Organization_Changed;
            _catalogSubscribed = true;
        }

        _catalogUpdateTimer.Start();
        if (!_layoutSubscribed)
        {
            ProjectsGrid.LayoutUpdated += ProjectsGrid_LayoutUpdated;
            _layoutSubscribed = true;
        }

        MarkProjectCardsReady();
    }

    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _catalogUpdateTimer.Stop();
        if (_catalogSubscribed)
        {
            AppServices.Catalog.Changed -= Catalog_Changed;
            AppServices.Organization.Changed -= Organization_Changed;
            _catalogSubscribed = false;
        }

        if (_layoutSubscribed)
        {
            ProjectsGrid.LayoutUpdated -= ProjectsGrid_LayoutUpdated;
            _layoutSubscribed = false;
        }
    }

    private void Catalog_Changed(object? sender, CatalogSnapshot snapshot) =>
        Interlocked.Exchange(ref _pendingCatalogSnapshot, snapshot);

    private void CatalogUpdateTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        var snapshot = Interlocked.Exchange(ref _pendingCatalogSnapshot, null);
        if (snapshot is null || !_catalogSubscribed)
        {
            return;
        }

        ViewModel.ApplySnapshot(snapshot);
        MarkProjectCardsReady();
    }

    private void MarkProjectCardsReady()
    {
        if (ViewModel.Projects.Count > 0
            && ProjectsGrid.Visibility == Visibility.Visible
            && ProjectsGrid.ContainerFromIndex(0) is FrameworkElement card
            && card.ActualWidth > 0
            && card.ActualHeight > 0)
        {
            StartupTimeline.Current.Mark(StartupTimeline.FirstProjectCardsReady);
        }
    }

    private void ProjectsGrid_LayoutUpdated(object? sender, object e) => MarkProjectCardsReady();

    private void Organization_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => UpdateProjectQuery());

    private void ProjectFilterBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateProjectQuery();

    private void ProjectSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProjectQuery();

    private void UpdateProjectQuery()
    {
        var sort = ProjectSortBox.SelectedIndex switch
        {
            1 => ProjectSortOrder.Activity,
            2 => ProjectSortOrder.RecentlyOpened,
            3 => ProjectSortOrder.Health,
            4 => ProjectSortOrder.Version,
            _ => ProjectSortOrder.Name,
        };
        ViewModel.UpdateQuery(ProjectFilterBox.Text, sort);
    }

    private void ProjectsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ProjectCardItem card)
        {
            return;
        }

        var project = card.Model;
        if (ProjectsGrid.ContainerFromItem(card) is not FrameworkElement anchor)
        {
            return;
        }

        var explorer = new MenuFlyoutItem { Text = "Open in Explorer", Icon = new FontIcon { Glyph = "\uE838" } };
        explorer.Click += (_, _) =>
        {
            if (ProjectOpenActions.OpenInExplorer(project.Path))
            {
                _ = AppServices.Organization.MarkOpened(project.Path);
            }
        };

        var vs = new MenuFlyoutItem { Text = "Open in Visual Studio", Icon = new FontIcon { Glyph = "\uE756" } };
        var solution = ProjectOpenActions.FindSolutionOrProject(project.Path);
        if (solution is null)
        {
            vs.Text = "No solution to open";
            vs.IsEnabled = false;
        }
        else
        {
            vs.Click += (_, _) =>
            {
                if (ProjectOpenActions.OpenWithDefaultApp(solution))
                {
                    _ = AppServices.Organization.MarkOpened(project.Path);
                }
            };
        }

        var menu = new MenuFlyout();
        menu.Items.Add(explorer);
        menu.Items.Add(vs);
        menu.ShowAt(anchor);
    }
}
