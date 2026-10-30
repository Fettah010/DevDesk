using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>What one export produced, for the UI to report and for a test to assert.</summary>
public sealed record ExportResult(
    string Path,
    IReadOnlyList<string> Entries,
    long Bytes,
    bool Truncated,
    int LogFileCount)
{
    public string Summary => Truncated
        ? $"Exported {LogFileCount} log file(s) and the environment report ({Bytes / 1024} KB, size cap reached)."
        : $"Exported {LogFileCount} log file(s) and the environment report ({Bytes / 1024} KB).";
}

/// <summary>
/// Builds the "report" bundle (roadmap §6.5.5): log files, the settings document
/// with every secret replaced, the environment report, and a manifest.
/// <para>
/// Every entry name is relative and derived from a bare file name, so a zip can
/// never contain an absolute path, a drive letter or a <c>..</c> segment — the
/// rule that stops an unzip-anywhere tool from writing outside the folder the
/// user picked. The 25 MB cap is enforced while collecting, not after.
/// </para>
/// </summary>
public static class DiagnosticsExport
{
    public const long MaxBytes = 25L * 1024 * 1024;
    public const int MaxLogFiles = LoggingService.RetainedFileCount;
    public const string ManifestFileName = "manifest.json";
    public const string EnvironmentFileName = "environment.txt";
    public const string SettingsFileName = "settings.redacted.json";
    public const string LogsFolderName = "logs";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        // The default encoder escapes "+" (and non-ASCII) as \uXXXX, which turns a
        // readable timestamp into noise. Nothing here is embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions SettingsSnapshotOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Where an export goes when the user just clicks Export: a timestamped file
    /// under the data directory, so there is no picker to get wrong and no
    /// permission prompt for a folder DevDesk may not own.
    /// </summary>
    public static string DefaultDestination(string? dataDirectory = null) => Path.Combine(
        dataDirectory ?? SettingsService.ResolveDataDirectory(),
        "exports",
        $"DevDesk-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

    /// <summary>
    /// Writes the bundle to <paramref name="destinationPath"/>.
    /// <paramref name="settings"/> defaults to a snapshot of the live settings
    /// file; tests pass their own so nothing real is read.
    /// </summary>
    public static async Task<ExportResult> CreateAsync(
        string destinationPath,
        string logDirectory,
        EnvironmentReport report,
        JsonObject? settings = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        var entries = new List<string>();
        var bytes = 0L;
        var truncated = false;
        var logCount = 0;

        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);

        foreach (var log in SelectLogFiles(logDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = $"{LogsFolderName}/{Path.GetFileName(log)}";
            var size = new FileInfo(log).Length;

            if (bytes + size > MaxBytes)
            {
                truncated = true;
                break;
            }

            // Redact on the way out as well as on the way in: a log written by an
            // older build (or hand-edited) must not leak through the bundle.
            var text = await ReadRedactedAsync(log, cancellationToken).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes(text);

            if (bytes + payload.Length > MaxBytes)
            {
                truncated = true;
                break;
            }

            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            await using (var stream = entry.Open())
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            bytes += payload.Length;
            logCount++;
            entries.Add(name);
        }

        await WriteTextAsync(archive, EnvironmentFileName, report.ToText(), cancellationToken).ConfigureAwait(false);
        entries.Add(EnvironmentFileName);

        var settingsJson = RedactSettings(settings ?? LoadSettingsSnapshot());
        await WriteTextAsync(
                archive,
                SettingsFileName,
                settingsJson.ToJsonString(SettingsSnapshotOptions),
                cancellationToken)
            .ConfigureAwait(false);
        entries.Add(SettingsFileName);

        // The manifest lists itself, so "files" and "entryCount" agree with what
        // the archive actually contains.
        entries.Add(ManifestFileName);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["app"] = AppInfo.Current.DisplayVersion,
            ["channel"] = report.Value("Update channel") ?? ReleaseChannels.Default,
            ["createdAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            ["entryCount"] = entries.Count,
            ["logFiles"] = logCount,
            ["truncated"] = truncated,
            ["files"] = new JsonArray(entries.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
        };

        await WriteTextAsync(
                archive,
                ManifestFileName,
                manifest.ToJsonString(ManifestOptions),
                cancellationToken)
            .ConfigureAwait(false);

        var total = new FileInfo(destinationPath).Length;
        LoggingService.Log.Information(
            "Diagnostics exported to {Path}: {Entries} entries, {Bytes} bytes, truncated {Truncated}",
            destinationPath, entries.Count, total, truncated);

        return new ExportResult(destinationPath, entries, total, truncated, logCount);
    }

    /// <summary>Newest first, capped at <see cref="MaxLogFiles"/>.</summary>
    public static IReadOnlyList<string> SelectLogFiles(string logDirectory)
    {
        try
        {
            if (!Directory.Exists(logDirectory))
            {
                return Array.Empty<string>();
            }

            return new DirectoryInfo(logDirectory)
                .GetFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxLogFiles)
                .Select(file => file.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Replaces every secret-looking string value, and blanks the values of keys
    /// that are secret by name. DevDesk never stores a token in JSON, so this is
    /// defence in depth rather than the mechanism.
    /// </summary>
    public static JsonObject RedactSettings(JsonObject raw)
    {
        var clone = (JsonObject)raw.DeepClone();
        Redact(clone);
        return clone;

        static void Redact(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(pair => pair.Key).ToList())
                    {
                        var value = obj[key];
                        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                        {
                            obj[key] = IsSecretKey(key) && text.Length > 0
                                ? LogRedaction.Placeholder
                                : LogRedaction.Redact(text);
                        }
                        else
                        {
                            Redact(value);
                        }
                    }

                    break;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        Redact(item);
                    }

                    break;
            }
        }

        static bool IsSecretKey(string key)
        {
            // Suffix/exact matching, not Contains: "path" must not be mistaken for
            // a "pat" (and blanked) just because the letters appear inside it.
            var lower = key.ToLowerInvariant();

            return lower is "token" or "tokens" or "pat" or "password" or "secret" or "apikey" or "api_key" or "auth"
                || lower.EndsWith("token", StringComparison.Ordinal)
                || lower.EndsWith("secret", StringComparison.Ordinal)
                || lower.EndsWith("password", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The settings as they are in effect: the file when there is one, otherwise
    /// the live model (a first run has no file yet, and a bundle that says "{}"
    /// helps nobody).
    /// </summary>
    private static JsonObject LoadSettingsSnapshot()
    {
        try
        {
            var path = SettingsService.Current.SettingsPath;
            if (File.Exists(path))
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject parsed)
                {
                    return parsed;
                }
            }

            return SettingsProjection.Merge(SettingsService.Current.Settings, new JsonObject());
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warning(ex, "Could not snapshot settings for export");
            return new JsonObject();
        }
    }

    private static async Task<string> ReadRedactedAsync(string path, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            // FileShare.ReadWrite matters: the live Serilog file sink holds the
            // current log open for writing, and a reader that asks for FileShare.Read
            // is refused ("being used by another process") even though the writer
            // allows sharing. The active log file is exactly the one worth having.
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"({Path.GetFileName(path)} could not be read: {ex.Message}){Environment.NewLine}";
        }

        var builder = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            builder.Append(LogRedaction.Redact(line.TrimEnd('\r'))).Append('\n');
        }

        return builder.ToString();
    }

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string entryName,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
