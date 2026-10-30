using Microsoft.UI.Xaml.Controls;

namespace DevDesk.Pages;

/// <summary>
/// Where GitHub releases and Action runs will live. The page is a shape plus an
/// empty state until v0.5.0: the previous version showed invented runs and
/// releases, which is worse than showing nothing, because it cannot be told apart
/// from a real read.
/// </summary>
public sealed partial class GitHubPage : Page
{
    public GitHubPage()
    {
        this.InitializeComponent();
    }
}
