using Microsoft.UI.Xaml;

namespace DevDesk.Services;

public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// Applies the requested theme to the window content (the root element).
/// The main window registers itself as <see cref="Root"/> so pages and the
/// title-bar toggle can flip the theme without knowing the window.
/// </summary>
public static class ThemeService
{
    public static FrameworkElement? Root { get; set; }

    public static void Apply(AppTheme theme) => ApplyTheme(ToElement(theme));

    public static void ApplyTheme(ElementTheme element)
    {
        if (Root is not null)
            Root.RequestedTheme = element;
    }

    public static ElementTheme ToElement(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };
}