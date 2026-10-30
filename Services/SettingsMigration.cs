using System.Text.Json.Nodes;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// Upgrades an on-disk settings object to the current schema, in memory.
/// <para>
/// Generation 0 is the pre-schema file (<c>{ "theme": "Dark" }</c>, no
/// <c>schemaVersion</c>): its single key already has the same name and meaning,
/// so the upgrade is "stamp the version and let the missing keys default". A file
/// from a newer build is left alone — dropping or rewriting keys it wrote would
/// be the one thing a downgrade must never do.
/// </para>
/// </summary>
public static class SettingsMigration
{
    /// <summary>
    /// Returns the object to project into <see cref="AppSettings"/>.
    /// <paramref name="migrated"/> is true when the on-disk object was upgraded.
    /// </summary>
    public static JsonObject Upgrade(JsonObject raw, out bool migrated)
    {
        migrated = false;

        var version = ReadVersion(raw);

        if (version >= AppSettings.CurrentSchemaVersion)
        {
            if (version > AppSettings.CurrentSchemaVersion)
            {
                LoggingService.Log.Warning(
                    "Settings were written by a newer DevDesk (schema {Version}); unknown keys are kept as-is",
                    version);
            }

            return raw;
        }

        // Generation 0 → 1: only the version is missing.
        raw["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        migrated = true;
        LoggingService.Log.Information("Settings upgraded from schema {Version} to {Current}",
            version, AppSettings.CurrentSchemaVersion);

        return raw;
    }

    /// <summary>0 when the key is absent or not a number — the generation-0 file.</summary>
    public static int ReadVersion(JsonObject raw) =>
        raw["schemaVersion"] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;
}
