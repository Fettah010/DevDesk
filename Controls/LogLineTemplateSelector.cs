using DevDesk.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDesk.Controls;

/// <summary>
/// Picks the row template for a console line. Kept as a selector rather than a
/// converter so the error colour comes from a theme resource in XAML, never from
/// a hardcoded brush (XamlResourceTests), and so switching wrap mode is a
/// template swap instead of a property walk over realized rows.
/// </summary>
public sealed partial class LogLineTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Normal { get; set; }

    public DataTemplate? Error { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is LogLine { IsError: true } ? Error : Normal;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
