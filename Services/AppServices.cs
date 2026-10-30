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
    private static SettingsService? _settings;

    public static ProjectCatalog Catalog => _catalog ??= new ProjectCatalog();

    public static SettingsService Settings => _settings ??= SettingsService.Current;

    public static UpdateService Updates => UpdateService.Current;

    /// <summary>
    /// Wires the service graph. Called once by the app; tests pass their own
    /// instances (or ignore this class entirely).
    /// </summary>
    public static void Initialize(ProjectCatalog? catalog = null, SettingsService? settings = null)
    {
        if (settings is not null)
        {
            _settings = settings;
        }

        _catalog = catalog ?? _catalog ?? new ProjectCatalog();

        // The settings file decides which release channel the updater follows
        // (D5: beta before 1.0). The setter refuses values outside the
        // vocabulary, so a hand-edited channel cannot break the updater.
        Updates.Channel = Settings.Channel;

        if (Settings.LastLoad.HasWarning)
        {
            LoggingService.Log.Warning("Settings: {Warning}", Settings.LastLoad.Warning);
        }
    }
}
