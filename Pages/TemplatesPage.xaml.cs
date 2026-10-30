using Microsoft.UI.Xaml.Controls;

namespace DevDesk.Pages;

/// <summary>
/// The template wizard's shape, labelled as such. The form is editable because the
/// preview underneath it is honest — it shows the path a project would land in;
/// the two actions that would touch the disk (browse, create) are disabled until
/// v0.4.0, so nothing here can pretend to have created something.
/// </summary>
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
}
