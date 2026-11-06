using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevDesk.Models;

namespace DevDesk.Services;

public sealed record ProjectOrganizationEntry(
    bool IsPinned,
    bool IsArchived,
    IReadOnlyList<string> Tags,
    string TagColor,
    DateTimeOffset? LastOpened);

public sealed record SavedProjectView(string Name, string Query, ProjectSortOrder SortOrder);

/// <summary>Persists user-owned project metadata separately from scanned Git facts.</summary>
public sealed class ProjectOrganizationStore
{
    public const int SchemaVersion = 1;
    public static IReadOnlyList<string> TagColors { get; } = new[] { "Blue", "Teal", "Purple", "Orange" };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
    private readonly JsonStore _store;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SavedProjectView> _savedViews = new();
    private bool _hideArchived;

    public ProjectOrganizationStore(string dataDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _store = new JsonStore(Path.Combine(dataDirectory, "project-organization.json"));
        _time = time ?? TimeProvider.System;
        Load();
    }

    public event EventHandler? Changed;

    public bool HideArchived
    {
        get
        {
            lock (_gate)
            {
                return _hideArchived;
            }
        }
    }

    public IReadOnlyList<SavedProjectView> SavedViews
    {
        get
        {
            lock (_gate)
            {
                return _savedViews.ToArray();
            }
        }
    }

    public IReadOnlyDictionary<string, ProjectOrganizationEntry> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToModel(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public ProjectOrganizationEntry Get(string checkoutPath)
    {
        var path = PathHygiene.Canonicalize(checkoutPath);
        lock (_gate)
        {
            return _entries.TryGetValue(path, out var entry)
                ? entry.ToModel()
                : new ProjectOrganizationEntry(false, false, Array.Empty<string>(), TagColors[0], null);
        }
    }

    public bool SetPinned(string checkoutPath, bool isPinned) =>
        Update(checkoutPath, entry => entry.IsPinned = isPinned);

    public bool SetArchived(string checkoutPath, bool isArchived) =>
        Update(checkoutPath, entry => entry.IsArchived = isArchived);

    public bool SetTagColor(string checkoutPath, string color)
    {
        if (!TagColors.Contains(color, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return Update(checkoutPath, entry => entry.TagColor = TagColors
            .First(value => string.Equals(value, color, StringComparison.OrdinalIgnoreCase)));
    }

    public bool SetTags(string checkoutPath, IEnumerable<string> tags)
        => SetTags(checkoutPath, tags, Get(checkoutPath).TagColor);

    public bool SetTags(string checkoutPath, IEnumerable<string> tags, string color)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (!TagColors.Contains(color, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = tags
            .Select(tag => tag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedColor = TagColors.First(value =>
            string.Equals(value, color, StringComparison.OrdinalIgnoreCase));
        return Update(checkoutPath, entry =>
        {
            entry.Tags = normalized.ToList();
            entry.TagColor = normalizedColor;
        });
    }

    public bool MarkOpened(string checkoutPath)
    {
        var openedAt = _time.GetUtcNow();
        return Update(checkoutPath, entry => entry.LastOpened = openedAt);
    }

    public bool SetHideArchived(bool hide)
    {
        lock (_gate)
        {
            var old = _hideArchived;
            _hideArchived = hide;
            try
            {
                SaveLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _hideArchived = old;
                LoggingService.Log.Error(ex, "Could not save archived-project preference to {Path}", _store.Path);
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool SaveView(SavedProjectView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var name = view.Name.Trim();
        if (name.Length == 0 || !Enum.IsDefined(view.SortOrder))
        {
            return false;
        }

        lock (_gate)
        {
            var old = _savedViews.ToArray();
            var existing = _savedViews.FindIndex(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            var normalized = view with { Name = name, Query = view.Query.Trim() };
            if (existing < 0)
            {
                _savedViews.Add(normalized);
            }
            else
            {
                _savedViews[existing] = normalized;
            }

            try
            {
                SaveLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _savedViews.Clear();
                _savedViews.AddRange(old);
                LoggingService.Log.Error(ex, "Could not save project list view to {Path}", _store.Path);
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool RemoveView(string name)
    {
        lock (_gate)
        {
            var index = _savedViews.FindIndex(view =>
                string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            var removed = _savedViews[index];
            _savedViews.RemoveAt(index);
            try
            {
                SaveLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _savedViews.Insert(index, removed);
                LoggingService.Log.Error(ex, "Could not remove project list view from {Path}", _store.Path);
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool Update(string checkoutPath, Action<Entry> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var path = PathHygiene.Canonicalize(checkoutPath);
        lock (_gate)
        {
            var old = _entries.TryGetValue(path, out var existing) ? existing.Clone() : null;
            var next = existing?.Clone() ?? new Entry();
            update(next);
            if (!next.IsPinned && !next.IsArchived && next.Tags.Count == 0 && next.LastOpened is null)
            {
                _entries.Remove(path);
            }
            else
            {
                _entries[path] = next;
            }

            try
            {
                SaveLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (old is null)
                {
                    _entries.Remove(path);
                }
                else
                {
                    _entries[path] = old;
                }

                LoggingService.Log.Error(ex, "Could not save project organization metadata to {Path}", _store.Path);
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Load()
    {
        var result = _store.Load();
        if (result.Corrupt)
        {
            LoggingService.Log.Warning(
                "Project organization data was corrupt ({Error}); defaults are in use. Backup: {Backup}",
                result.Error,
                result.BackupPath ?? "(unavailable)");
            return;
        }

        if (result.Root is null)
        {
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                LoggingService.Log.Warning("Project organization data could not be read ({Error})", result.Error);
            }

            return;
        }

        try
        {
            var version = result.Root["schemaVersion"]?.GetValue<int>() ?? 0;
            if (version != SchemaVersion)
            {
                LoggingService.Log.Warning(
                    "Project organization schema {Schema} is not supported (expected {Expected})",
                    version,
                    SchemaVersion);
                return;
            }

            if (result.Root["projects"] is not JsonObject projects)
            {
                throw new JsonException("Project organization data has no projects object.");
            }

            _hideArchived = result.Root["hideArchived"]?.GetValue<bool>() ?? false;
            if (result.Root["savedViews"] is JsonArray savedViews)
            {
                foreach (var node in savedViews)
                {
                    if (node is not JsonObject saved
                        || string.IsNullOrWhiteSpace(saved["name"]?.GetValue<string>())
                        || !Enum.TryParse(saved["sortOrder"]?.GetValue<string>(), true, out ProjectSortOrder sortOrder))
                    {
                        throw new JsonException("Project organization data contains an invalid saved view.");
                    }

                    _savedViews.Add(new SavedProjectView(
                        saved["name"]!.GetValue<string>().Trim(),
                        saved["query"]?.GetValue<string>() ?? string.Empty,
                        sortOrder));
                }
            }

            foreach (var (storedPath, node) in projects)
            {
                if (node is not JsonObject item || string.IsNullOrWhiteSpace(storedPath))
                {
                    throw new JsonException("Project organization data contains an invalid entry.");
                }

                var canonicalPath = PathHygiene.Canonicalize(storedPath);
                var tags = item["tags"] is JsonArray tagArray
                    ? tagArray
                        .Select(tag => tag?.GetValue<string>())
                        .Where(tag => !string.IsNullOrWhiteSpace(tag))
                        .Select(tag => tag!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();
                _entries[canonicalPath] = new Entry
                {
                    IsPinned = item["isPinned"]?.GetValue<bool>() ?? false,
                    IsArchived = item["isArchived"]?.GetValue<bool>() ?? false,
                    Tags = tags,
                    TagColor = NormalizeTagColor(item["tagColor"]?.GetValue<string>()),
                    LastOpened = item["lastOpened"]?.GetValue<DateTimeOffset?>(),
                };
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            _entries.Clear();
            _savedViews.Clear();
            _hideArchived = false;
            LoggingService.Log.Warning(ex, "Project organization data was invalid; defaults are in use");
        }
    }

    private void SaveLocked()
    {
        var projects = new JsonObject();
        foreach (var (path, entry) in _entries)
        {
            projects[path] = JsonSerializer.SerializeToNode(entry, SerializerOptions);
        }

        _store.Save(new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["hideArchived"] = _hideArchived,
            ["savedViews"] = new JsonArray(_savedViews.Select(view => (JsonNode?)new JsonObject
            {
                ["name"] = view.Name,
                ["query"] = view.Query,
                ["sortOrder"] = view.SortOrder.ToString(),
            }).ToArray()),
            ["projects"] = projects,
        });
    }

    private static string NormalizeTagColor(string? color) =>
        TagColors.FirstOrDefault(value => string.Equals(value, color, StringComparison.OrdinalIgnoreCase))
        ?? TagColors[0];

    private sealed class Entry
    {
        public bool IsPinned { get; set; }
        public bool IsArchived { get; set; }
        public List<string> Tags { get; set; } = new();
        public string TagColor { get; set; } = TagColors[0];
        public DateTimeOffset? LastOpened { get; set; }

        public ProjectOrganizationEntry ToModel() => new(IsPinned, IsArchived, Tags.ToArray(), TagColor, LastOpened);

        public Entry Clone() => new()
        {
            IsPinned = IsPinned,
            IsArchived = IsArchived,
            Tags = Tags.ToList(),
            TagColor = TagColor,
            LastOpened = LastOpened,
        };
    }
}
