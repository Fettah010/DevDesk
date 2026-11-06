using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace DevDesk.Services;

/// <summary>
/// The rules that decide what the scan is allowed to look at. They live here
/// rather than inside <see cref="ScanEngine"/> so a test can assert on a rule
/// without building a repository, and so the walk and the marker count can
/// never disagree about what "ignored" means.
/// </summary>
public static class PathHygiene
{
    /// <summary>
    /// Default scan exclusions. <c>copilot-worktrees</c> is deliberate
    /// (roadmap D8): those checkouts can be scanned from an explicitly
    /// configured root, but should not appear as duplicate cards in the default
    /// ecosystem scan.
    /// </summary>
    public static IReadOnlyList<string> IgnoredFolderNames { get; } = new ReadOnlyCollection<string>(new[]
    {
        "bin", "obj", ".vs", ".vscode", ".git", "node_modules", "packages",
        "Releases", "ReleasesLocal", "Logs", "dist", "build", "TestResults",
        "_ReSharper", "copilot-worktrees",
    });

    private static readonly char[] Separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    /// <summary>
    /// True for the ignore list and for every dot-folder (<c>.git</c>, <c>.vs</c>,
    /// <c>.idea</c>, …): hidden tooling is never a project.
    /// </summary>
    public static bool IsIgnoredFolderName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        if (name[0] == '.')
        {
            return true;
        }

        return IgnoredFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Rules that cannot be relaxed by per-root ignore negation.</summary>
    public static bool IsSafetyIgnoredFolderName(string? name) =>
        string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A repo is a folder whose <c>.git</c> entry exists — as a directory
    /// (normal clone) or as a file (worktree, submodule, <c>gitdir:</c> pointer).
    /// The file form is why the worktrees under <c>copilot-worktrees</c> used to
    /// be invisible.
    /// </summary>
    public static bool IsRepositoryRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var dotGit = Path.Combine(directory, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    /// <summary>Canonical form used for every path comparison (no trailing separator).</summary>
    public static string Canonicalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Separators);

    /// <summary>
    /// <paramref name="path"/> is <paramref name="parent"/> or sits below it.
    /// Compares whole path segments, so <c>C:\a\DevDesk2</c> is not inside
    /// <c>C:\a\DevDesk</c>.
    /// </summary>
    public static bool IsUnder(string path, string parent)
    {
        var candidate = Canonicalize(path);
        var root = Canonicalize(parent);

        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A link/junction. Following one can loop or leave the scan root.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Directory listing that never throws and is ordered, for determinism.</summary>
    public static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        return TryEnumerateDirectories(directory, out var children, out _)
            ? children
            : Array.Empty<string>();
    }

    public static bool TryEnumerateDirectories(
        string directory,
        out string[] children,
        out string? error)
    {
        try
        {
            children = Directory.GetDirectories(directory);
            Array.Sort(children, StringComparer.OrdinalIgnoreCase);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            children = Array.Empty<string>();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>File listing that never throws and is ordered, for determinism.</summary>
    public static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
