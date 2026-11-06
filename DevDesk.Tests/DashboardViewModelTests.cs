using System;
using System.Linq;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using DevDesk.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The Dashboard must render the shared snapshot, never start its own scan:
/// that is what makes Dashboard → DevDesk → Dashboard cheap and consistent.
/// </summary>
public class DashboardViewModelTests
{
    [Fact]
    public async Task Dashboard_LoadsFromCatalog_WithoutScanning()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);
        engine.Projects = new[]
        {
            ProjectCatalogTests.Project("clean"),
            ProjectCatalogTests.Project("dirty", dirty: true),
        };

        var viewModel = new DashboardViewModel(catalog);
        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.Calls);
        Assert.Equal(2, viewModel.Projects.Count);
        Assert.Equal("2", viewModel.ProjectCountText);
        Assert.Equal("1", viewModel.CleanCountText);
        Assert.Equal("1", viewModel.AttentionCountText);
        Assert.Equal("2", viewModel.RemoteCountText);
        Assert.Equal("2 across enabled roots", viewModel.ProjectsPillText);
        Assert.Contains("need attention", viewModel.Subtitle, StringComparison.Ordinal);
        Assert.Contains("Updated ", viewModel.LastRefreshText, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasError);
        Assert.Equal(Visibility.Visible, viewModel.ProjectsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.EmptyVisibility);
    }

    [Fact]
    public async Task Dashboard_SecondVisit_DoesNotRescan()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);
        engine.Projects = new[] { ProjectCatalogTests.Project("clean") };

        await new DashboardViewModel(catalog).LoadCommand.ExecuteAsync(null);
        await new DashboardViewModel(catalog).LoadCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.Calls);
        Assert.Equal(1, catalog.ScanCount);
    }

    [Fact]
    public async Task Dashboard_RefreshCommand_Rescans()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);
        var viewModel = new DashboardViewModel(catalog);

        await viewModel.LoadCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, engine.Calls);
        Assert.Equal(2, catalog.ScanCount);
    }

    [Fact]
    public async Task Dashboard_ErrorState_HasRetry()
    {
        var engine = new FakeScanEngine { Error = new InvalidOperationException("the disk is on fire") };
        var catalog = new ProjectCatalog(engine);
        var viewModel = new DashboardViewModel(catalog);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasError);
        Assert.Equal("the disk is on fire", viewModel.ErrorMessage);
        Assert.Equal(Visibility.Visible, viewModel.ErrorVisibility);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsEmpty); // an error is not an empty state
        Assert.True(viewModel.RefreshCommand.CanExecute(null));

        engine.Error = null;
        engine.Projects = new[] { ProjectCatalogTests.Project("clean") };
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasError);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(Visibility.Collapsed, viewModel.ErrorVisibility);
        Assert.Single(viewModel.Projects);
        Assert.Equal(2, catalog.ScanCount);
    }

    [Fact]
    public async Task Dashboard_EmptyEcosystem_ShowsEmptyState()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);
        var viewModel = new DashboardViewModel(catalog);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Projects);
        Assert.True(viewModel.IsEmpty);
        Assert.Equal(Visibility.Visible, viewModel.EmptyVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.ProjectsVisibility);
        Assert.Equal("0", viewModel.ProjectCountText);
        Assert.Contains("No project folders", viewModel.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_AllClean_SaysSo()
    {
        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);
        engine.Projects = new[]
        {
            ProjectCatalogTests.Project("one"),
            ProjectCatalogTests.Project("two"),
        };

        var viewModel = new DashboardViewModel(catalog);
        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("0", viewModel.AttentionCountText);
        Assert.Equal("All projects are healthy.", viewModel.Subtitle);
    }

    [Fact]
    public async Task Dashboard_SearchShowsMatchingResultsAndAnHonestNoMatchState()
    {
        var engine = new FakeScanEngine
        {
            Projects = new[]
            {
                ProjectCatalogTests.Project("Alpha"),
                ProjectCatalogTests.Project("Beta"),
            },
        };
        var viewModel = new DashboardViewModel(new ProjectCatalog(engine));
        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.UpdateQuery("beta", ProjectSortOrder.Name);

        Assert.Equal("Beta", Assert.Single(viewModel.Projects).Model.Name);
        Assert.Equal("1 of 2 projects", viewModel.ProjectsPillText);
        Assert.Equal(Visibility.Visible, viewModel.ProjectsVisibility);

        viewModel.UpdateQuery("no match", ProjectSortOrder.Name);

        Assert.Empty(viewModel.Projects);
        Assert.Equal("No matching projects", viewModel.EmptyTitle);
        Assert.Equal("Try a different search or clear the filter.", viewModel.EmptyMessage);
        Assert.Equal(Visibility.Visible, viewModel.EmptyVisibility);
        Assert.Equal("0 of 2 projects", viewModel.ProjectsPillText);
    }

    [Fact]
    public void Dashboard_IncrementalSnapshot_ReusesUnchangedCardsAndReplacesChangedCards()
    {
        var catalog = new ProjectCatalog(new FakeScanEngine());
        var viewModel = new DashboardViewModel(catalog);
        var alpha = ProjectCatalogTests.Project("Alpha");
        var beta = ProjectCatalogTests.Project("Beta");
        var emptyProjects = viewModel.Projects;
        viewModel.ApplySnapshot(new CatalogSnapshot(new[] { alpha, beta }, null));

        Assert.NotSame(emptyProjects, viewModel.Projects);
        var alphaCard = viewModel.Projects[0];
        var betaCard = viewModel.Projects[1];
        var updatedAlpha = new ProjectModel
        {
            Name = "Zeta",
            Path = alpha.Path,
            Branch = "feature",
            Version = alpha.Version,
        };

        viewModel.ApplySnapshot(new CatalogSnapshot(
            new[] { updatedAlpha, beta },
            null,
            freshness: CatalogFreshness.Partial));

        Assert.NotSame(alphaCard, viewModel.Projects[1]);
        Assert.Equal("Zeta", viewModel.Projects[1].Name);
        Assert.Equal("feature", viewModel.Projects[1].Branch);
        Assert.Same(betaCard, viewModel.Projects[0]);

        viewModel.UpdateQuery("beta", ProjectSortOrder.Name);
        Assert.Same(betaCard, Assert.Single(viewModel.Projects));

        viewModel.UpdateQuery(string.Empty, ProjectSortOrder.Name);
        Assert.Same(betaCard, viewModel.Projects[0]);
        Assert.NotSame(alphaCard, viewModel.Projects[1]);
    }

    [Fact]
    public async Task Dashboard_JoinsTheLastRunFromHistoryAndNeverBorrowsANumber()
    {
        using var sandbox = new SettingsSandbox();
        var history = new BuildHistory(sandbox.Directory);
        history.Add(new BuildRecord(
            "/tmp/built",
            "built",
            OperationKind.Build,
            OperationStatus.Failed,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(2.4),
            1,
            "error CS1002: ; expected",
            null,
            "Debug",
            "x64"));

        var engine = new FakeScanEngine();
        var catalog = new ProjectCatalog(engine);
        engine.Projects = new[]
        {
            ProjectCatalogTests.Project("built"),
            ProjectCatalogTests.Project("never"),
        };

        var viewModel = new DashboardViewModel(catalog, history);
        await viewModel.LoadCommand.ExecuteAsync(null);

        var built = viewModel.Projects.First(card => card.Model.Name == "built");
        var never = viewModel.Projects.First(card => card.Model.Name == "never");

        Assert.Equal("build failed", built.BuildBadgeText);
        Assert.Equal(Visibility.Visible, built.FailedVisibility);
        Assert.Equal(Visibility.Collapsed, built.OkVisibility);

        // A project that has never built says so; it does not inherit "build
        // failed" from the card next to it (gotcha 16).
        Assert.Equal("no build yet", never.BuildBadgeText);
        Assert.Equal(Visibility.Visible, never.MutedVisibility);
        Assert.Equal(Visibility.Collapsed, never.FailedVisibility);
    }

    [Fact]
    public void Dashboard_WithoutAHistoryShowsNoBuildYet()
    {
        var catalog = new ProjectCatalog(new FakeScanEngine());
        var viewModel = new DashboardViewModel(catalog);

        Assert.Empty(viewModel.Projects);
        Assert.Equal("no build yet", new ProjectCardItem(ProjectCatalogTests.Project("x"), null).BuildBadgeText);
    }

    [Fact]
    public void Dashboard_DescribesTheScanRootItReads()
    {
        var catalog = new ProjectCatalog(new FakeScanEngine());
        var viewModel = new DashboardViewModel(catalog);

        Assert.Contains("enabled scan root(s)", viewModel.ScanPathText, StringComparison.Ordinal);
        Assert.Contains("excludes DevDesk", viewModel.ScanPathText, StringComparison.Ordinal);
    }
}
