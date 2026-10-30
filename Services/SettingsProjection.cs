using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// The mapping between the on-disk JSON and <see cref="AppSettings"/>.
/// <para>
/// It is deliberately hand-written and starts every save from a clone of the
/// object that was read, so a key written by a newer build survives a round trip
/// through this one. <c>JsonSerializer.Deserialize&lt;AppSettings&gt;</c> cannot
/// do that: it drops anything it does not know about.
/// </para>
/// <para>
/// Every getter is tolerant: a hand-edited number where a string belongs becomes
/// the default instead of an exception, because a settings file must never stop
/// the app from starting.
/// </para>
/// </summary>
public static class SettingsProjection
{
    public static AppSettings Read(JsonObject root) => new()
    {
        SchemaVersion = GetInt(root, "schemaVersion", AppSettings.CurrentSchemaVersion),
        Theme = GetString(root, "theme", nameof(AppTheme.System)),
        Channel = GetString(root, "channel", ReleaseChannels.Default),
        Platform = GetString(root, "platform", "x64"),
        Roots = ReadRoots(root["roots"] as JsonArray),
        DefaultReleaseChannel = GetString(root, "defaultReleaseChannel", ReleaseChannels.Default),
        Window = ReadWindow(root["window"] as JsonObject),
        LastRoute = GetString(root, "lastRoute", "dashboard"),
        Diagnostics = ReadDiagnostics(root["diagnostics"] as JsonObject),
        Features = ReadFeatures(root["features"] as JsonObject),
    };

    /// <summary>
    /// Copies <paramref name="settings"/> into <paramref name="existing"/>, which
    /// is the object that came from disk. Everything DevDesk does not know about
    /// is carried through untouched.
    /// </summary>
    public static JsonObject Merge(AppSettings settings, JsonObject existing)
    {
        var target = (JsonObject)existing.DeepClone();

        // Never lower the stamp: a file written by a newer build keeps its
        // version, so that build still knows a migration is (not) needed.
        target["schemaVersion"] = Math.Max(settings.SchemaVersion, AppSettings.CurrentSchemaVersion);
        target["theme"] = settings.Theme;
        target["channel"] = settings.Channel;
        target["platform"] = settings.Platform;
        target["roots"] = WriteRoots(settings.Roots, target["roots"] as JsonArray);
        target["defaultReleaseChannel"] = settings.DefaultReleaseChannel;
        target["window"] = WriteWindow(settings.Window, target["window"] as JsonObject);
        target["lastRoute"] = settings.LastRoute;
        target["diagnostics"] = WriteDiagnostics(settings.Diagnostics, target["diagnostics"] as JsonObject);
        target["features"] = WriteFeatures(settings.Features, target["features"] as JsonObject);

        return target;
    }

    private static List<ScanRootSettings> ReadRoots(JsonArray? array)
    {
        var roots = new List<ScanRootSettings>();

        foreach (var entry in array ?? new JsonArray())
        {
            if (entry is not JsonObject obj)
            {
                continue;
            }

            var root = new ScanRootSettings
            {
                Path = GetString(obj, "path", ScanEngine.DefaultScanRoot),
                Depth = GetInt(obj, "depth", ScanEngine.DefaultMaxDepth),
            };

            if (obj["ignores"] is JsonArray ignores)
            {
                var names = ignores
                    .Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text!)
                    .ToList();

                if (names.Count > 0)
                {
                    root.Ignores = names;
                }
            }

            roots.Add(root);
        }

        return roots.Count > 0 ? roots : new List<ScanRootSettings> { new() };
    }

    private static JsonArray WriteRoots(List<ScanRootSettings> roots, JsonArray? existing)
    {
        var array = new JsonArray();

        for (var index = 0; index < roots.Count; index++)
        {
            var item = index < (existing?.Count ?? 0) && existing![index] is JsonObject previous
                ? (JsonObject)previous.DeepClone()
                : new JsonObject();

            item["path"] = roots[index].Path;
            item["depth"] = roots[index].Depth;
            item["ignores"] = new JsonArray(roots[index].Ignores
                .Select(name => (JsonNode?)JsonValue.Create(name))
                .ToArray());

            array.Add(item);
        }

        return array;
    }

    private static WindowSettings ReadWindow(JsonObject? obj) => obj is null
        ? new WindowSettings()
        : new WindowSettings
        {
            X = GetInt(obj, "x", 120),
            Y = GetInt(obj, "y", 80),
            Width = GetInt(obj, "width", 1280),
            Height = GetInt(obj, "height", 820),
            Maximized = GetBool(obj, "maximized", false),
        };

    private static JsonObject WriteWindow(WindowSettings window, JsonObject? existing)
    {
        var target = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        target["x"] = window.X;
        target["y"] = window.Y;
        target["width"] = window.Width;
        target["height"] = window.Height;
        target["maximized"] = window.Maximized;
        return target;
    }

    private static DiagnosticsSettings ReadDiagnostics(JsonObject? obj) => obj is null
        ? new DiagnosticsSettings()
        : new DiagnosticsSettings
        {
            LogLevel = GetString(obj, "logLevel", "Information"),
            KeepDays = GetInt(obj, "keepDays", 14),
        };

    private static JsonObject WriteDiagnostics(DiagnosticsSettings diagnostics, JsonObject? existing)
    {
        var target = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        target["logLevel"] = diagnostics.LogLevel;
        target["keepDays"] = diagnostics.KeepDays;
        return target;
    }

    private static FeatureSettings ReadFeatures(JsonObject? obj) => obj is null
        ? new FeatureSettings()
        : new FeatureSettings
        {
            BackgroundHealthCheck = GetBool(obj, "backgroundHealthCheck", false),
            MinimizeToTray = GetBool(obj, "minimizeToTray", false),
        };

    private static JsonObject WriteFeatures(FeatureSettings features, JsonObject? existing)
    {
        var target = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        target["backgroundHealthCheck"] = features.BackgroundHealthCheck;
        target["minimizeToTray"] = features.MinimizeToTray;
        return target;
    }

    private static string GetString(JsonObject obj, string key, string fallback) =>
        obj.TryGetPropertyValue(key, out var node)
        && node is JsonValue value
        && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : fallback;

    private static int GetInt(JsonObject obj, string key, int fallback) =>
        obj.TryGetPropertyValue(key, out var node)
        && node is JsonValue value
        && value.TryGetValue<int>(out var number)
            ? number
            : fallback;

    private static bool GetBool(JsonObject obj, string key, bool fallback) =>
        obj.TryGetPropertyValue(key, out var node)
        && node is JsonValue value
        && value.TryGetValue<bool>(out var flag)
            ? flag
            : fallback;
}
