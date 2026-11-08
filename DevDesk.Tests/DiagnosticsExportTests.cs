using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The export bundle is what a user attaches to a bug report, so it has to open,
/// name its entries relatively, stay under the size cap, and contain nothing
/// secret. Each test gets its own directory and never touches the real profile.
/// </summary>
public class DiagnosticsExportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "devdesk-export-" + Guid.NewGuid().ToString("N"));

    private readonly string _logs;
    private readonly string _out;

    public DiagnosticsExportTests()
    {
        _logs = Path.Combine(_root, "Logs");
        _out = Path.Combine(_root, "exports");
        Directory.CreateDirectory(_logs);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    [Fact]
    public async Task Export_ContainsExpectedEntries()
    {
        File.WriteAllText(Path.Combine(_logs, "applog-20261030.log"), "line one\nline two\n");
        File.WriteAllText(Path.Combine(_logs, "applog-20261029.log"), "older\n");

        var settings = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["theme"] = "Dark",
            ["roots"] = new JsonArray(new JsonObject
            {
                ["path"] = ScanEngine.DefaultScanRoot,
                ["depth"] = 2,
            }),
        };

        var report = EnvironmentReport.Collect("git version 2.47.1", "1.2.161", ReleaseChannels.Beta, settingsPath: "settings.json");
        var destination = Path.Combine(_out, "bundle.zip");

        var result = await DiagnosticsExport.CreateAsync(destination, _logs, report, settings);

        Assert.True(File.Exists(destination));
        Assert.Equal(2, result.LogFileCount);
        Assert.False(result.Truncated);
        Assert.Contains(DiagnosticsExport.ManifestFileName, result.Entries);
        Assert.Equal(5, result.Entries.Count);

        using var archive = ZipFile.OpenRead(destination);
        var names = archive.Entries.Select(entry => entry.FullName).ToList();

        Assert.Contains("logs/applog-20261030.log", names);
        Assert.Contains("logs/applog-20261029.log", names);
        Assert.Contains(DiagnosticsExport.EnvironmentFileName, names);
        Assert.Contains(DiagnosticsExport.SettingsFileName, names);
        Assert.Contains(DiagnosticsExport.ManifestFileName, names);

        var manifest = JsonNode.Parse(Read(archive, DiagnosticsExport.ManifestFileName)) as JsonObject;
        Assert.NotNull(manifest);
        Assert.Equal(AppInfo.Current.DisplayVersion, manifest!["app"]!.GetValue<string>());
        Assert.Equal(ReleaseChannels.Beta, manifest["channel"]!.GetValue<string>());
        Assert.Equal(5, manifest["entryCount"]!.GetValue<int>());
        Assert.False(manifest["truncated"]!.GetValue<bool>());

        var environment = Read(archive, DiagnosticsExport.EnvironmentFileName);
        Assert.Contains("git: git version 2.47.1", environment, StringComparison.Ordinal);
        Assert.Contains("vpk: 1.2.161", environment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_HasNoAbsolutePaths()
    {
        File.WriteAllText(Path.Combine(_logs, "applog-20261030.log"), "x\n");

        var destination = Path.Combine(_out, "bundle.zip");
        var result = await DiagnosticsExport.CreateAsync(
            destination,
            _logs,
            EnvironmentReport.Collect(settingsPath: "settings.json"),
            new JsonObject());

        Assert.NotEmpty(result.Entries);

        using var archive = ZipFile.OpenRead(destination);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;

            Assert.False(Path.IsPathRooted(name), $"{name} is rooted");
            Assert.DoesNotContain(':', name);
            Assert.DoesNotContain("..", name);
            Assert.False(name.StartsWith('/'), $"{name} is rooted");
            Assert.False(name.StartsWith('\\'), $"{name} is rooted");
            Assert.DoesNotContain(Path.GetTempPath(), name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Export_ScrubsSecretsFromLogsAndSettings()
    {
        File.WriteAllText(
            Path.Combine(_logs, "applog-20261030.log"),
            "clone https://owner:hunter2@github.com/o/r.git\n" +
            "token ghp_abcDEF1234567890abcDEF1234567890\n" +
            "GET https://api.github.com/x?token=abc123\n");

        var settings = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["githubToken"] = "ghp_abcDEF1234567890abcDEF1234567890",
            ["rootToken"] = "plain-text-token",
            ["roots"] = new JsonArray(new JsonObject { ["path"] = @"C:\Code\projects\DevEcosystem" }),
        };

        var destination = Path.Combine(_out, "bundle.zip");
        await DiagnosticsExport.CreateAsync(
            destination,
            _logs,
            EnvironmentReport.Collect(settingsPath: "settings.json"),
            settings);

        using var archive = ZipFile.OpenRead(destination);

        var log = Read(archive, "logs/applog-20261030.log");
        Assert.DoesNotContain("hunter2", log, StringComparison.Ordinal);
        Assert.DoesNotContain("abcDEF1234567890abcDEF1234567890", log, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", log, StringComparison.Ordinal);

        var settingsJson = Read(archive, DiagnosticsExport.SettingsFileName);
        Assert.DoesNotContain("abcDEF1234567890abcDEF1234567890", settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-text-token", settingsJson, StringComparison.Ordinal);

        // The non-secret values survive: "path" must not be mistaken for a "pat"
        // token and blanked.
        Assert.Contains(@"C:\\Code\\projects\\DevEcosystem", settingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_ReadsTheActiveLogFileWhileTheSinkStillHasItOpen()
    {
        var active = Path.Combine(_logs, "applog-20261030.log");
        await File.WriteAllTextAsync(active, "live line\n");

        // This is what Serilog's file sink does: append with shared write access.
        await using var writer = new FileStream(
            active,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);

        var destination = Path.Combine(_out, "bundle.zip");
        var result = await DiagnosticsExport.CreateAsync(
            destination,
            _logs,
            EnvironmentReport.Collect(settingsPath: "settings.json"),
            new JsonObject());

        Assert.Equal(1, result.LogFileCount);

        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains("live line", Read(archive, "logs/applog-20261030.log"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_StopsAtTheSizeCap()
    {
        var big = Path.Combine(_logs, "applog-20261030.log");
        File.WriteAllText(big, new string('x', (int)DiagnosticsExport.MaxBytes + 1024));

        var destination = Path.Combine(_out, "bundle.zip");
        var result = await DiagnosticsExport.CreateAsync(
            destination,
            _logs,
            EnvironmentReport.Collect(settingsPath: "settings.json"),
            new JsonObject());

        Assert.True(result.Truncated);
        Assert.Equal(0, result.LogFileCount);

        using var archive = ZipFile.OpenRead(destination);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, entry => entry.FullName == DiagnosticsExport.ManifestFileName);
    }

    [Fact]
    public void EnvironmentReport_ListsVersions()
    {
        var report = EnvironmentReport.Collect(
            gitVersion: "git version 2.47.1.windows.1",
            vpkVersion: "1.2.161",
            channel: ReleaseChannels.Dev,
            scanRoot: ScanEngine.DefaultScanRoot,
            settingsPath: @"C:\data\settings.json");

        Assert.Equal(AppInfo.Current.DisplayVersion, report.Value("DevDesk"));
        Assert.Equal("git version 2.47.1.windows.1", report.Value("git"));
        Assert.False(string.IsNullOrWhiteSpace(report.Value("git exe")));
        Assert.Equal("1.2.161", report.Value("vpk"));
        Assert.Equal(ReleaseChannels.Dev, report.Value("Update channel"));
        Assert.Equal(ScanEngine.DefaultScanRoot, report.Value("Scan root"));
        Assert.False(string.IsNullOrWhiteSpace(report.Value(".NET runtime")));
        Assert.False(string.IsNullOrWhiteSpace(report.Value("OS")));
        Assert.False(string.IsNullOrWhiteSpace(report.Value("Windows App SDK")));
        Assert.False(string.IsNullOrWhiteSpace(report.Value("Process architecture")));

        var text = report.ToText();
        Assert.Contains("DevDesk: ", text, StringComparison.Ordinal);
        Assert.Contains(".NET runtime: ", text, StringComparison.Ordinal);
        Assert.Contains("vpk: 1.2.161", text, StringComparison.Ordinal);
        Assert.Contains("git exe: ", text, StringComparison.Ordinal);
        Assert.Equal(15, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void EnvironmentReport_SaysWhenAToolIsMissing()
    {
        var report = EnvironmentReport.Collect(settingsPath: "settings.json");

        Assert.Equal("not found", report.Value("git"));
        Assert.Equal("not found", report.Value("vpk"));
        Assert.Null(report.Value("There is no such label"));
    }

    private static string Read(ZipArchive archive, string entryName)
    {
        var entry = archive.Entries.FirstOrDefault(e => e.FullName == entryName)
            ?? throw new InvalidOperationException($"{entryName} is missing from the bundle.");

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
