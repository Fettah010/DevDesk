using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDesk.Pages;

public sealed partial class TemplatesPage : Page
{
    public TemplatesPage()
    {
        this.InitializeComponent();
        UpdatePreview();
    }

    private void NewAppName_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        var name = string.IsNullOrWhiteSpace(NewAppName.Text) ? "MyNewApp" : NewAppName.Text.Trim();
        PreviewText.Text = $"→ {NewAppLocation.Text.TrimEnd('\\')}\\{name}";
    }

    private void WizardAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string action)
            return;

        TemplatesInfoBar.Title = action switch
        {
            "create" => "Project scaffolding lands in Phase 1",
            "browse" => "Folder picker lands in Phase 1",
            _ => "Cancelled"
        };
        TemplatesInfoBar.Message = action == "cancel"
            ? "Nothing was created."
            : "TemplateService will copy the template and token-replace names, then git init + smoke build.";
        TemplatesInfoBar.IsOpen = true;
    }
}