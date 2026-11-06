using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>Finds actual solution/project files without following links or generated folders.</summary>
public static class WorkspaceTargetDiscovery
{
    private const int MaxDepth = 4;
    private const int MaxTargets = 200;
    private static readonly HashSet<string> SolutionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx",
    };
    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj", ".vcxproj",
    };

    public static IReadOnlyList<WorkspaceTarget> Discover(string checkoutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutPath);
        var root = PathHygiene.Canonicalize(checkoutPath);
        if (!Directory.Exists(root))
        {
            return Array.Empty<WorkspaceTarget>();
        }

        var targets = new Dictionary<string, WorkspaceTarget>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && targets.Count < MaxTargets)
        {
            var (directory, depth) = queue.Dequeue();
            foreach (var file in PathHygiene.SafeEnumerateFiles(directory))
            {
                var extension = Path.GetExtension(file);
                var kind = SolutionExtensions.Contains(extension)
                    ? "Solution"
                    : ProjectExtensions.Contains(extension)
                        ? "Project"
                        : null;
                if (kind is not null)
                {
                    targets.TryAdd(file, new WorkspaceTarget(Path.GetFileName(file), file, kind));
                }
            }

            if (depth >= MaxDepth || targets.Count >= MaxTargets)
            {
                continue;
            }

            foreach (var child in PathHygiene.SafeEnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (PathHygiene.IsIgnoredFolderName(name) || PathHygiene.IsReparsePoint(child))
                {
                    continue;
                }

                queue.Enqueue((child, depth + 1));
            }
        }

        return targets.Values
            .OrderBy(target => target.Kind == "Solution" ? 0 : 1)
            .ThenBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
