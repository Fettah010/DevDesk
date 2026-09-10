using DevDesk.Services;
using Microsoft.UI.Xaml;
using Xunit;

namespace DevDesk.Tests;

public class ThemeServiceTests
{
    [Fact]
    public void ToElement_MapsSupportedThemes()
    {
        Assert.Equal(ElementTheme.Light, ThemeService.ToElement(AppTheme.Light));
        Assert.Equal(ElementTheme.Dark, ThemeService.ToElement(AppTheme.Dark));
        Assert.Equal(ElementTheme.Default, ThemeService.ToElement(AppTheme.System));
    }
}