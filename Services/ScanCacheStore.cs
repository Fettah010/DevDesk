using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>Disposable, versioned cache for the project catalog (schema 2).</summary>
public sealed class ScanCacheStore
{
    public const int SchemaVersion = 2;
    private const int MaxProjects = 10_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly JsonStore _store;
    private readonly TimeProvider _time;

    public ScanCacheStore(JsonStore store, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
    }

    public string CachePath => _store.Path;

    public CatalogSnapshot? Load(IReadOnlyList<ScanRootSettings> roots)
    {
        var result = _store.Load();
        if (result.Corrupt)
        {
            LoggingService.Log.Warning(
                "Scan cache was corrupt ({Error}); rebuilding from local roots. Backup: {Backup}",
                result.Error,
                result.BackupPath ?? "(unavailable)");
            return null;
        }

        if (result.Root is null)
        {
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                LoggingService.Log.Warning("Scan cache could not be read ({Error}); rebuilding", result.Error);
            }

            return null;
        }

        var schema = result.Root["schemaVersion"] is JsonValue value
            && value.TryGetValue<int>(out var version)
                ? version
                : 0;
        if (schema != SchemaVersion)
        {
            LoggingService.Log.Warning(
                "Scan cache schema {Schema} is not supported (expected {Expected}); rebuilding",
                schema,
                SchemaVersion);
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<CacheDocument>(result.Root.ToJsonString(), Options);
            if (document is null
                || document.Projects is null
                || document.Templates is null
                || document.Projects.Count > MaxProjects)
            {
                throw new JsonException("The scan cache has an invalid shape or exceeds the project limit.");
            }

            var enabledRoots = roots
                .Where(root => root.Enabled && !string.IsNullOrWhiteSpace(root.Path))
                .Select(root => SafeCanonicalize(root.Path))
                .Where(path => path is not null)
                .Cast<string>()
                .ToArray();

            var projects = document.Projects
                .Where(entry => IsInsideConfiguredRoot(entry.Path, enabledRoots))
                .Select(entry => entry.ToModel())
                .ToList();
            var templates = document.Templates
                .Where(entry => IsInsideConfiguredRoot(entry.FullPath, enabledRoots))
                .Select(entry => entry.ToModel())
                .ToList();
            var self = document.Self?.ToModel();

            return new CatalogSnapshot(
                projects,
                self,
                templates,
                document.CapturedAt,
                TimeSpan.Zero,
                CatalogFreshness.Cached,
                document.RootDiagnostics);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            LoggingService.Log.Warning(ex, "Scan cache shape was invalid; rebuilding from local roots");
            return null;
        }
    }

    public void Save(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            var document = new CacheDocument
            {
                SchemaVersion = SchemaVersion,
                CapturedAt = snapshot.CapturedAt,
                Projects = snapshot.Projects.Select(ProjectEntry.From).ToList(),
                Self = snapshot.Self is null ? null : ProjectEntry.From(snapshot.Self),
                Templates = snapshot.Templates.Select(TemplateEntry.From).ToList(),
                RootDiagnostics = snapshot.RootDiagnostics.ToList(),
            };
            var node = JsonSerializer.SerializeToNode(document, Options) as JsonObject
                ?? throw new JsonException("Could not serialize the scan cache.");
            _store.Save(node);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.Log.Warning(ex, "Scan cache could not be saved to {Path}", _store.Path);
        }
    }

    private static bool IsInsideConfiguredRoot(string path, IReadOnlyList<string> roots)
    {
        var candidate = SafeCanonicalize(path);
        return candidate is not null && roots.Any(root => PathHygiene.IsUnder(candidate, root));
    }

    private static string? SafeCanonicalize(string path)
    {
        try
        {
            return PathHygiene.Canonicalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
        public List<ProjectEntry> Projects { get; set; } = new();
        public ProjectEntry? Self { get; set; }
        public List<TemplateEntry> Templates { get; set; } = new();
        public List<ScanRootDiagnostic> RootDiagnostics { get; set; } = new();
    }

    private sealed class ProjectEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string ScanRootPath { get; set; } = string.Empty;
        public bool DetailsPending { get; set; }
        public string RepositoryIdentity { get; set; } = string.Empty;
        public bool IsWorktree { get; set; }
        public bool IsSubmodule { get; set; }
        public string? WorktreeOfPath { get; set; }
        public string IconGlyph { get; set; } = "\uE8B7";
        public bool IsWorkspace { get; set; }
        public string Branch { get; set; } = "—";
        public string Version { get; set; } = "—";
        public bool VersionMismatch { get; set; }
        public bool HasRemote { get; set; }
        public bool RemoteOk { get; set; }
        public string RemoteHost { get; set; } = "Local only";
        public bool Dirty { get; set; }
        public bool BehindUpstream { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public List<CommitEntry> RecentCommits { get; set; } = new();
        public List<ComponentEntry> Components { get; set; } = new();

        public static ProjectEntry From(ProjectModel model) => new()
        {
            Name = model.Name,
            Path = model.Path,
            ScanRootPath = model.ScanRootPath,
            DetailsPending = model.DetailsPending,
            RepositoryIdentity = model.RepositoryIdentity,
            IsWorktree = model.IsWorktree,
            IsSubmodule = model.IsSubmodule,
            WorktreeOfPath = model.WorktreeOfPath,
            IconGlyph = model.IconGlyph,
            IsWorkspace = model.IsWorkspace,
            Branch = model.Branch,
            Version = model.Version,
            VersionMismatch = model.VersionMismatch,
            HasRemote = model.HasRemote,
            RemoteOk = model.RemoteOk,
            RemoteHost = model.RemoteHost,
            Dirty = model.Dirty,
            BehindUpstream = model.BehindUpstream,
            StatusText = model.StatusText,
            RecentCommits = model.RecentCommits.Select(CommitEntry.From).ToList(),
            Components = model.Components.Select(ComponentEntry.From).ToList(),
        };

        public ProjectModel ToModel()
        {
            if (string.IsNullOrWhiteSpace(Name) || SafeCanonicalize(Path) is null)
            {
                throw new JsonException("A cached project has an invalid name or path.");
            }

            return new ProjectModel
            {
                Name = Name,
                Path = Path,
                ScanRootPath = ScanRootPath,
                DetailsPending = DetailsPending,
                RepositoryIdentity = RepositoryIdentity,
                IsWorktree = IsWorktree,
                IsSubmodule = IsSubmodule,
                WorktreeOfPath = WorktreeOfPath,
                IconGlyph = IconGlyph,
                IsWorkspace = IsWorkspace,
                Branch = Branch,
                Version = Version,
                VersionMismatch = VersionMismatch,
                HasRemote = HasRemote,
                RemoteOk = RemoteOk,
                RemoteHost = RemoteHost,
                Dirty = Dirty,
                BehindUpstream = BehindUpstream,
                StatusText = StatusText,
                RecentCommits = RecentCommits.Select(entry => entry.ToModel()).ToList(),
                Components = Components.Select(entry => entry.ToModel()).ToList(),
            };
        }
    }

    private sealed class CommitEntry
    {
        public string Subject { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public DateTimeOffset? CommittedAt { get; set; }
        public static CommitEntry From(CommitInfo item) => new()
        {
            Subject = item.Subject,
            Author = item.Author,
            Date = item.Date,
            Hash = item.Hash,
            CommittedAt = item.CommittedAt,
        };
        public CommitInfo ToModel() => new()
        {
            Subject = Subject,
            Author = Author,
            Date = Date,
            Hash = Hash,
            CommittedAt = CommittedAt,
        };
    }

    private sealed class ComponentEntry
    {
        public string Name { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public static ComponentEntry From(ComponentInfo item) => new()
        {
            Name = item.Name,
            RelativePath = item.RelativePath,
            Kind = item.Kind,
        };
        public ComponentInfo ToModel() => new()
        {
            Name = Name,
            RelativePath = RelativePath,
            Kind = Kind,
        };
    }

    private sealed class TemplateEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public static TemplateEntry From(TemplateDescriptor item) => new()
        {
            Name = item.Name,
            Description = item.Description,
            Version = item.Version,
            FullPath = item.FullPath,
        };
        public TemplateDescriptor ToModel()
        {
            if (string.IsNullOrWhiteSpace(Name) || SafeCanonicalize(FullPath) is null)
            {
                throw new JsonException("A cached template has an invalid name or path.");
            }

            return new TemplateDescriptor
            {
                Name = Name,
                Description = Description,
                Version = Version,
                FullPath = FullPath,
            };
        }
    }
}
