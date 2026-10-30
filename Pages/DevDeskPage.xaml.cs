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

    // The roadmap, not the retired plan: this link must point at a file that
    // exists, or it is a dead button that looks live.
    private void OpenPlanButton_Click(object sender, RoutedEventArgs e)
    {
        var roadmapPath = Path.Combine(ViewModel.SelfPath, "docs", "DEVDESK-ROADMAP.md");
        if (ProjectOpenActions.OpenWithDefaultApp(roadmapPath))
        {
            return;
        }

        ShowWarning("Roadmap not found", roadmapPath);
    }

    private void ShowWarning(string title, string message)
    {
        ActionBar.Severity = InfoBarSeverity.Warning;
        ActionBar.Title = title;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }
}
