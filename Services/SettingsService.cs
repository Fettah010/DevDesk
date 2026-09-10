using System;
using System.IO;
using System.Text.Json;

namespace DevDesk.Services;

/// <summary>
/// Small JSON-backed settings store (%LocalAppData%\DevDesk\settings.json).
/// Only the theme is persisted today; scan roots, ignore patterns and the
/// token come in later phases. Writing is best-effort (never throws).
/// </summary>
public sealed class SettingsService
{
    public static SettingsService Current { get; } = new();

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevDesk");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private AppTheme _theme = AppTheme.System;

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            Save();
        }
    }

    private SettingsService() => Load();

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("theme", out var t) &&
                Enum.TryParse<AppTheme>(t.GetString(), out var parsed))
            {
                _theme = parsed;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Failed to load settings - using defaults");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { theme = _theme.ToString() }));
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Failed to save settings");
        }
    }
}