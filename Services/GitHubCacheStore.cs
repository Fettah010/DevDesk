using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevDesk.Services;

public sealed record GitHubCacheEntry(
    string Payload,
    string? ETag,
    DateTimeOffset FetchedAt);

public sealed class GitHubCacheStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 100;
    public const int MaximumCacheCharacters = 20 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JsonStore _store;
    private readonly object _gate = new();
    private Dictionary<string, GitHubCacheEntry> _entries;

    public GitHubCacheStore(string path)
    {
        _store = new JsonStore(path);
        _entries = LoadEntries();
    }

    public GitHubCacheEntry? Get(string key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    public void Set(string key, GitHubCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (entry.Payload.Length > GitHubApiClient.MaximumCachedPayloadBytes)
            {
                throw new InvalidDataException("GitHub cache entry exceeded the per-response size limit.");
            }

            _entries[key] = entry;
            while (_entries.Count > MaximumEntries || TotalCharacters() > MaximumCacheCharacters)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.FetchedAt).FirstOrDefault();
                if (oldest.Key is null || oldest.Key == key && _entries.Count == 1)
                    throw new InvalidDataException("GitHub cache exceeded its total size limit.");
                _entries.Remove(oldest.Key);
            }

            SaveLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            SaveLocked();
        }
    }

    private Dictionary<string, GitHubCacheEntry> LoadEntries()
    {
        try
        {
            if (File.Exists(_store.Path) && new FileInfo(_store.Path).Length > MaximumCacheCharacters * 2L)
            {
                LoggingService.Log.Warning("GitHub cache exceeded its size limit and was ignored");
                return new Dictionary<string, GitHubCacheEntry>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Log.Warning("GitHub cache could not be inspected");
            return new Dictionary<string, GitHubCacheEntry>(StringComparer.Ordinal);
        }

        var result = _store.Load();
        if (result.Corrupt || result.Root is null)
        {
            return new Dictionary<string, GitHubCacheEntry>(StringComparer.Ordinal);
        }

        try
        {
            var document = result.Root.Deserialize<CacheDocument>(JsonOptions);
            if (document?.SchemaVersion != CurrentSchemaVersion || document.Entries is null)
            {
                return new Dictionary<string, GitHubCacheEntry>(StringComparer.Ordinal);
            }

            return new Dictionary<string, GitHubCacheEntry>(
                document.Entries
                    .Where(pair => pair.Value is not null
                        && pair.Value.Payload.Length <= GitHubApiClient.MaximumCachedPayloadBytes
                        && pair.Key.Length <= 512)
                    .Take(MaximumEntries)
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            LoggingService.Log.Warning("GitHub cache was not in a supported format; cached data was ignored");
            return new Dictionary<string, GitHubCacheEntry>(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        var document = new CacheDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Entries = _entries
        };
        var node = JsonSerializer.SerializeToNode(document, JsonOptions) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidDataException("GitHub cache could not be serialized.");
        _store.Save(node);
    }

    private int TotalCharacters() =>
        _entries.Sum(pair => pair.Key.Length + pair.Value.Payload.Length + (pair.Value.ETag?.Length ?? 0));

    private sealed class CacheDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("entries")]
        public Dictionary<string, GitHubCacheEntry>? Entries { get; set; } =
            new(StringComparer.Ordinal);
    }
}
