using System;
using System.IO;
using System.Text.Json.Nodes;
using DevDesk.Services;

namespace DevDesk.Tests;

/// <summary>
/// A throw-away data directory for settings tests. Constructing the service with
/// an explicit directory is the whole point of the injectable storage root: no
/// test ever reads or writes the real %LocalAppData%\DevDesk.
/// </summary>
internal sealed class SettingsSandbox : IDisposable
{
    public string Directory { get; } =
        Path.Combine(Path.GetTempPath(), "devdesk-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsSandbox() => System.IO.Directory.CreateDirectory(Directory);

    public string SettingsPath => Path.Combine(Directory, "settings.json");

    public SettingsService NewService() => new(Directory);

    public void WriteFile(string json) => File.WriteAllText(SettingsPath, json);

    public string ReadFile() => File.ReadAllText(SettingsPath);

    public JsonObject ReadJson() =>
        JsonNode.Parse(ReadFile()) as JsonObject
        ?? throw new InvalidOperationException("settings.json is not a JSON object");

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
