using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDesk.Pages;

public sealed partial class GitHubPage : Page
{
    public GitHubPage()
    {
        this.InitializeComponent();
    }

    // PLACEHOLDER: ComboBox below is static until GitHubService (Phase 2)
    // provides the real list of repos that have a remote.
    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        TokenInfoBar.Message = "Public API limit is 60 requests/hour. Live data streams in with Phase 2.";
        TokenInfoBar.IsOpen = true;
    }
}