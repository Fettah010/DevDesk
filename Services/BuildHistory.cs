using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// The remembered last builds (W-0.3.7), capped and written atomically.
/// <para>
/// This file is the only source of a build badge. It is deliberately separate
/// from the scan cache and from <see cref="ProjectModel"/>: a scan must never
/// depend on a process that already ran (D-0.3.1), and deleting this file must
/// cost nothing but the badges.
/// </para>
/// <para>
/// Every failure path is a warning in the log, never an exception into a build:
/// losing history is a nuisance, losing the app is a bug.
/// </para>
/// </summary>
public sealed class BuildHistory
{
    public const int SchemaVersion = 1;
    public const int DefaultLimit = BuildSettings.DefaultHistoryLimit;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly JsonStore _store;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private List<BuildRecord> _entries = new();

    public BuildHistory(string? dataDirectory = null, TimeProvider? time = null)
        : this(
            new JsonStore(Path.Combine(
                string.IsNullOrWhiteSpace(dataDirectory) ? SettingsService.ResolveDataDirectory() : dataDirectory,
                "build-history.json")),
            time)
    {
    }

    public BuildHistory(JsonStore store, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
        Load();
    }

    public string HistoryPath => _store.Path;

    /// <summary>Newest first. Never null; empty when nothing has run yet.</summary>
    public IReadOnlyList<BuildRecord> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>The badge's answer, or null when this project has never built.</summary>
    public BuildRecord? LastFor(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        var wanted = Normalize(projectPath);
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(Normalize(entry.ProjectPath), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    public void Add(BuildRecord record, int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            _entries.Insert(0, record);

            var cap = Math.Max(1, limit);
            if (_entries.Count > cap)
            {
                _entries.RemoveRange(cap, _entries.Count - cap);
            }

            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = new List<BuildRecord>();
            Save();
        }
    }

    private void Load()
    {
        var result = _store.Load();

        if (result.Corrupt)
        {
            LoggingService.Log.Warning(
                "Build history was corrupt ({Error}); starting empty. A copy is kept at {Backup}",
                result.Error,
                result.BackupPath ?? "(none)");
            return;
        }

        if (result.Root is null)
        {
            return;
        }

        var version = result.Root["schemaVersion"] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : 0;

        if (version > SchemaVersion)
        {
            // Written by a newer build: discard rather than guess at a shape.
            LoggingService.Log.Warning(
                "Build history has schema {Version} (this build writes {Current}); it will be rebuilt",
                version,
                SchemaVersion);
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<HistoryDocument>(result.Root.ToJsonString(), SerializerOptions);
            if (document?.Entries is null)
            {
                return;
            }

            var entries = new List<BuildRecord>(document.Entries.Count);
            foreach (var entry in document.Entries)
            {
                var record = entry.ToRecord();
                if (record is not null)
                {
                    entries.Add(record);
                }
            }

            _entries = entries;
        }
        catch (JsonException ex)
        {
            LoggingService.Log.Warning(ex, "Build history could not be read; starting empty");
        }
    }

    private void Save()
    {
        try
        {
            var document = new HistoryDocument
            {
                SchemaVersion = SchemaVersion,
                WrittenAt = _time.GetUtcNow(),
                Entries = new List<HistoryEntry>(_entries.Count),
            };

            foreach (var entry in _entries)
            {
                document.Entries.Add(HistoryEntry.From(entry));
            }

            var node = JsonSerializer.SerializeToNode(document, SerializerOptions) as JsonObject;
            if (node is not null)
            {
                _store.Save(node);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.Log.Warning(ex, "Build history could not be saved to {Path}", _store.Path);
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private sealed class HistoryDocument
    {
        public int SchemaVersion { get; set; } = BuildHistory.SchemaVersion;

        public DateTimeOffset WrittenAt { get; set; }

        public List<HistoryEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// The on-disk shape. Deliberately its own type: renaming a property on
    /// <see cref="BuildRecord"/> must not silently orphan every stored entry.
    /// </summary>
    private sealed class HistoryEntry
    {
        public string ProjectPath { get; set; } = string.Empty;

        public string ProjectName { get; set; } = string.Empty;

        public string Kind { get; set; } = nameof(OperationKind.Build);

        public string Status { get; set; } = nameof(OperationStatus.Succeeded);

        public DateTimeOffset StartedAt { get; set; }

        public int DurationMs { get; set; }

        public int? ExitCode { get; set; }

        public string? FirstError { get; set; }

        public string? LogPath { get; set; }

        public string Configuration { get; set; } = "Debug";

        public string Platform { get; set; } = string.Empty;

        public static HistoryEntry From(BuildRecord record) => new()
        {
            ProjectPath = record.ProjectPath,
            ProjectName = record.ProjectName,
            Kind = record.Kind.ToString(),
            Status = record.Status.ToString(),
            StartedAt = record.StartedAt,
            DurationMs = (int)Math.Round(Math.Max(0, record.Duration.TotalMilliseconds)),
            ExitCode = record.ExitCode,
            FirstError = record.FirstError,
            LogPath = record.LogPath,
            Configuration = record.Configuration,
            Platform = record.Platform,
        };

        public BuildRecord? ToRecord()
        {
            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                return null;
            }

            var kind = Enum.TryParse<OperationKind>(Kind, ignoreCase: true, out var parsedKind)
                ? parsedKind
                : OperationKind.Build;

            var status = Enum.TryParse<OperationStatus>(Status, ignoreCase: true, out var parsedStatus)
                ? parsedStatus
                : OperationStatus.Succeeded;

            return new BuildRecord(
                ProjectPath,
                ProjectName,
                kind,
                status,
                StartedAt,
                TimeSpan.FromMilliseconds(Math.Max(0, DurationMs)),
                ExitCode,
                FirstError,
                LogPath,
                Configuration,
                Platform);
        }
    }
}
