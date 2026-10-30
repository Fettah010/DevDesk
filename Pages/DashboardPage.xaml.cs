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
    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = new DashboardViewModel(AppServices.Catalog);
        this.InitializeComponent();
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void ProjectsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ProjectModel project)
        {
            return;
        }

        if (ProjectsGrid.ContainerFromItem(project) is not FrameworkElement anchor)
        {
            return;
        }

        var explorer = new MenuFlyoutItem { Text = "Open in Explorer", Icon = new FontIcon { Glyph = "\uE838" } };
        explorer.Click += (_, _) => ProjectOpenActions.OpenInExplorer(project.Path);

        var vs = new MenuFlyoutItem { Text = "Open in Visual Studio", Icon = new FontIcon { Glyph = "\uE756" } };
        var solution = ProjectOpenActions.FindSolutionOrProject(project.Path);
        if (solution is null)
        {
            vs.Text = "No solution to open";
            vs.IsEnabled = false;
        }
        else
        {
            vs.Click += (_, _) => ProjectOpenActions.OpenWithDefaultApp(solution);
        }

        var menu = new MenuFlyout();
        menu.Items.Add(explorer);
        menu.Items.Add(vs);
        menu.ShowAt(anchor);
    }
}
