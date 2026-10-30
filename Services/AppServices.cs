using System;

namespace DevDesk.Services;

/// <summary>
/// The app's service graph, resolved by pages that must keep parameterless
/// constructors (<c>Frame.Navigate</c> needs them, D9).
/// <para>
/// Initialised once from <see cref="App"/>'s constructor, after logging.
/// Everything is created lazily, so a unit test can construct the services it
/// needs directly and never touches this class.
/// </para>
/// </summary>
public static class AppServices
{
    private static ProjectCatalog? _catalog;

    public static ProjectCatalog Catalog => _catalog ??= new ProjectCatalog();

    public static SettingsService Settings => SettingsService.Current;

    public static UpdateService Updates => UpdateService.Current;

    /// <summary>Replaces the catalog. Tests use this; the app calls it once.</summary>
    public static void Initialize(ProjectCatalog? catalog = null)
    {
        _catalog = catalog ?? _catalog ?? new ProjectCatalog();
    }
}
