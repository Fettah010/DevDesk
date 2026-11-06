using System;
using System.IO;
using System.Text.Json.Nodes;
using DevDesk.Models;

namespace DevDesk.Services;

public enum SettingsLoadStatus
{
    Missing,
    Loaded,
    Migrated,
    Corrupt,
}

/// <summary>What happened when the settings file was read, for the UI to report.</summary>
public sealed record SettingsLoadResult(SettingsLoadStatus Status, string? BackupPath = null, string? Warning = null)
{
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);
}

/// <summary>
/// JSON-backed settings (%LocalAppData%\DevDesk\settings.json, schema 1).
/// <para>
/// Reading never throws and never loses a file: a corrupt document is backed up
/// and replaced by defaults. Writing is atomic and preserves keys this build does
/// not know about (see <see cref="JsonStore"/> and
/// <see cref="SettingsProjection"/>).
/// </para>
/// <para>
/// The data directory is injectable (<c>DEVDESK_DATA_DIR</c> or the constructor)
/// so tests never touch the real profile.
/// </para>
/// </summary>
public sealed class SettingsService
{
    public const string DataDirectoryVariable = "DEVDESK_DATA_DIR";

    public static SettingsService Current { get; } = new();

    private readonly JsonStore _store;
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly PerfCounters _counters;

    private JsonObject _raw = new();
    private AppSettings _settings = new();

    public SettingsService(
        string? dataDirectory = null,
        TimeProvider? time = null,
        PerfCounters? counters = null)
    {
        var directory = string.IsNullOrWhiteSpace(dataDirectory) ? ResolveDataDirectory() : dataDirectory;
        _store = new JsonStore(Path.Combine(directory, "settings.json"));
        _time = time ?? TimeProvider.System;
        _counters = counters ?? PerfCounters.Current;
        LastLoad = Load();

        // Logging is initialised *after* this (the log directory lives beside
        // settings.json, so reading settings is what makes that path known), so
        // this mark waits in the timeline and is flushed with the others.
        StartupTimeline.Current.Mark(StartupTimeline.SettingsLoaded);
    }

    public AppSettings Settings => _settings;

    public SettingsLoadResult LastLoad { get; private set; } = new(SettingsLoadStatus.Missing);

    public string SettingsPath => _store.Path;

    public AppTheme Theme
    {
        get => Enum.TryParse<AppTheme>(_settings.Theme, ignoreCase: true, out var theme) ? theme : AppTheme.System;
        set
        {
            _settings.Theme = value.ToString();
            Save();
        }
    }

    /// <summary>Update channel. An unknown value falls back to the default rather than crashing.</summary>
    public string Channel
    {
        get => _settings.Channel;
        set
        {
            _settings.Channel = ReleaseChannels.IsKnown(value) ? value : ReleaseChannels.Default;
            Save();
        }
    }

    /// <summary>
    /// The destination to reopen on the next launch. Unknown tags (a hand edit, or a
    /// route from a newer build) fall back to the Dashboard rather than opening
    /// nothing, and a write only happens when the route really changed.
    /// </summary>
    public string LastRoute
    {
        get => Routes.Normalize(_settings.LastRoute);
        set
        {
            var route = Routes.Normalize(value);
            if (_settings.LastRoute == route)
            {
                return;
            }

            _settings.LastRoute = route;
            Save();
        }
    }

    /// <summary>Writes the current settings. Never throws: a failed save is logged.</summary>
    public void Save()
    {
        lock (_gate)
        {
            var started = _time.GetTimestamp();
            var outcome = PerfOutcome.Ok;

            try
            {
                _raw = SettingsProjection.Merge(_settings, _raw);
                _store.Save(_raw);
            }
            catch (Exception ex)
            {
                outcome = PerfOutcome.Failed;
                LoggingService.Log.Error(ex, "Failed to save settings to {Path}", _store.Path);
            }
            finally
            {
                // §6.3 budgets a settings write at 20 ms; the number is measured
                // around the real file write, under the same lock.
                _counters.RecordSettingsWrite(
                    _time.GetElapsedTime(started, _time.GetTimestamp()),
                    outcome);
            }
        }
    }

    public static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevDesk");
    }

    private SettingsLoadResult Load()
    {
        var result = _store.Load();

        if (result.Corrupt)
        {
            _raw = new JsonObject();
            _settings = new AppSettings();

            var warning = result.BackupPath is null
                ? "The settings file could not be read and was reset to defaults."
                : $"The settings file could not be read and was reset to defaults. A copy is kept at {result.BackupPath}.";

            LoggingService.Log.Warning("Settings file was corrupt ({Error}); defaults restored", result.Error);
            return new SettingsLoadResult(SettingsLoadStatus.Corrupt, result.BackupPath, warning);
        }

        if (result.Root is null)
        {
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                LoggingService.Log.Warning("Settings could not be read ({Error}); defaults in use", result.Error);
            }

            _raw = new JsonObject();
            _settings = new AppSettings();
            return new SettingsLoadResult(SettingsLoadStatus.Missing);
        }

        _raw = SettingsMigration.Upgrade(result.Root, out var migrated);
        _settings = SettingsProjection.Read(_raw);
        Normalize(_settings);

        return migrated
            ? new SettingsLoadResult(SettingsLoadStatus.Migrated)
            : new SettingsLoadResult(SettingsLoadStatus.Loaded);
    }

    /// <summary>Keeps hand-edited values inside the ranges the rest of the app assumes.</summary>
    private static void Normalize(AppSettings settings)
    {
        if (!ReleaseChannels.IsKnown(settings.Channel))
        {
            LoggingService.Log.Warning(
                "Unknown update channel {Channel} in settings; using {Default}",
                settings.Channel, ReleaseChannels.Default);
            settings.Channel = ReleaseChannels.Default;
        }

        if (!ReleaseChannels.IsKnown(settings.DefaultReleaseChannel))
        {
            settings.DefaultReleaseChannel = ReleaseChannels.Default;
        }

        settings.Theme = Enum.TryParse<AppTheme>(settings.Theme, ignoreCase: true, out var theme)
            ? theme.ToString()
            : nameof(AppTheme.System);

        foreach (var root in settings.Roots)
        {
            root.Depth = Math.Clamp(root.Depth, 1, 8);
            if (string.IsNullOrWhiteSpace(root.Path))
            {
                root.Path = ScanEngine.DefaultScanRoot;
            }

            root.Ignores ??= new System.Collections.Generic.List<string>(PathHygiene.IgnoredFolderNames);
        }
    }
}
