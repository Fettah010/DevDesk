using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using DevDesk.Pages;
using DevDesk.Services;

namespace DevDesk;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
        this.Title = "DevDesk";
        this.SystemBackdrop = new MicaBackdrop();

        // Native-feeling, theme-aware title bar: the app content extends into the
        // caption area, Mica shows through it, and the caption buttons (min/max/
        // close) stay OS-drawn with automatic light/dark colors.
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);
        this.AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        this.AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        this.AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        this.AppWindow.Resize(new SizeInt32(1240, 800));

        // Register this window's content as the theme root so ThemeService can
        // flip light/dark from any page.
        if (this.Content is FrameworkElement root)
            ThemeService.Root = root;
        ThemeService.Apply(SettingsService.Current.Theme);

        ContentFrame.Navigate(typeof(DashboardPage));
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
    }

    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is not string tag)
            return;

        var target = tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "projects" => typeof(ProjectsPage),
            "templates" => typeof(TemplatesPage),
            "github" => typeof(GitHubPage),
            "self" => typeof(DevDeskPage),
            "settings" => typeof(SettingsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            _ => null
        };
        if (target is null)
            return;

        ContentFrame.Navigate(target, null, new EntranceNavigationTransitionInfo());
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var next = SettingsService.Current.Theme switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System
        };

        SettingsService.Current.Theme = next;
        ThemeService.Apply(next);
    }
}