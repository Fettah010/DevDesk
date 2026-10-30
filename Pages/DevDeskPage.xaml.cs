using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DevDesk.Services;
using DevDesk.ViewModels;

namespace DevDesk.Pages;

/// <summary>
/// The app's own workspace page. State comes from <see cref="SelfViewModel"/>
/// (shared catalog); this file only opens folders/files and reports failures.
/// </summary>
public sealed partial class DevDeskPage : Page
{
    public SelfViewModel ViewModel { get; }

    public DevDeskPage()
    {
        ViewModel = new SelfViewModel(AppServices.Catalog);
        this.InitializeComponent();
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OpenExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOpenActions.OpenInExplorer(ViewModel.SelfPath))
        {
            return;
        }

        ShowWarning("Nothing to open", "The DevDesk repo folder was not found.");
    }

    private void OpenPlanButton_Click(object sender, RoutedEventArgs e)
    {
        var planPath = Path.Combine(ViewModel.SelfPath, "docs", "DEVDESK-PLAN.md");
        if (ProjectOpenActions.OpenWithDefaultApp(planPath))
        {
            return;
        }

        ShowWarning("Plan doc not found", planPath);
    }

    // Phase 1 (GitService / BuildService) wires these to real actions.
    private void StubAction_Click(object sender, RoutedEventArgs e)
    {
        ShowWarning(
            "Not wired yet",
            "Page actions arrive with the Phase 1 services (GitService / BuildService).");
    }

    private void ShowWarning(string title, string message)
    {
        ActionBar.Severity = InfoBarSeverity.Warning;
        ActionBar.Title = title;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }
}
