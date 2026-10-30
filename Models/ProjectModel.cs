using System;
using System.Collections.Generic;

namespace DevDesk.Models;

public sealed class CommitInfo
{
    public required string Subject { get; init; }
    public required string Author { get; init; }
    public required string Date { get; init; }
    public required string Hash { get; init; }
}

/// <summary>
/// A sub-part of a workspace repo (e.g. the test project or the docs of a
/// self-hosted app). Shown on the dedicated "DevDesk" page.
/// </summary>
public sealed class ComponentInfo
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string Kind { get; init; }

    public string Glyph => Kind switch
    {
        "app" => "\uE756",
        "tests" => "\uE9D9",
        "docs" => "\uE8A5",
        "scripts" => "\uE943",
        "workflow" => "\uE7F4",
        _ => "\uE8B7"
    };
}

/// <summary>
/// A detected project (or "workspace" — one git repo hosting several
/// solutions). Populated by <see cref="DevDesk.Services.ScanEngine"/> through
/// <see cref="DevDesk.Services.ProjectCatalog"/>: the projects list feeds
/// Dashboard/Projects, and the self workspace feeds the dedicated DevDesk page.
/// </summary>
public sealed class ProjectModel
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string IconGlyph { get; init; } = "\uE8B7";
    public bool IsWorkspace { get; init; }
    public string WorkspaceLabel => IsWorkspace ? "Workspace" : "Project";

    public required string Branch { get; init; }
    public required string Version { get; init; }
    public bool VersionMismatch { get; init; }
    public string VersionDisplay => $"v{Version}";

    public bool HasRemote { get; init; } = true;
    public bool RemoteOk { get; init; } = true;
    public bool IsNotRemoteOk => !RemoteOk;
    public string RemoteHost { get; init; } = "GitHub";

    public bool Dirty { get; init; }
    public bool BehindUpstream { get; init; }
    public string StatusText { get; init; } = "Healthy";

    public string ListSubtitle =>
        $"branch {Branch} · {(HasRemote ? RemoteHost : "local only")} · {StatusText}";

    public IReadOnlyList<CommitInfo> RecentCommits { get; init; } = Array.Empty<CommitInfo>();

    public IReadOnlyList<ComponentInfo> Components { get; init; } = Array.Empty<ComponentInfo>();
}