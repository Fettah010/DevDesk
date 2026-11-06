using System;
using System.IO;

namespace DevDesk.Services;

public sealed record RepositoryIdentityInfo(
    string GitDirectory,
    string CommonDirectory,
    bool IsWorktree,
    bool IsSubmodule,
    string? WorktreeOfPath);

/// <summary>Resolves local Git metadata identity without fetching or changing a repository.</summary>
public static class RepositoryIdentityResolver
{
    private const int MaxPointerLength = 4096;

    public static RepositoryIdentityInfo? Resolve(string checkoutPath)
    {
        if (string.IsNullOrWhiteSpace(checkoutPath))
        {
            return null;
        }

        var checkout = PathHygiene.Canonicalize(checkoutPath);
        var dotGit = Path.Combine(checkout, ".git");
        string gitDirectory;
        var metadataIsPointer = File.Exists(dotGit);

        if (Directory.Exists(dotGit))
        {
            gitDirectory = PathHygiene.Canonicalize(dotGit);
        }
        else if (metadataIsPointer)
        {
            var pointer = ReadSingleLine(dotGit);
            const string prefix = "gitdir:";
            if (pointer is null || !pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var target = pointer[prefix.Length..].Trim();
            if (target.Length == 0)
            {
                return null;
            }

            gitDirectory = PathHygiene.Canonicalize(
                Path.IsPathRooted(target) ? target : Path.Combine(checkout, target));
            if (!Directory.Exists(gitDirectory))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
        var hasCommonDirectory = File.Exists(commonDirectoryFile);
        var commonDirectory = gitDirectory;
        if (hasCommonDirectory)
        {
            var common = ReadSingleLine(commonDirectoryFile);
            if (string.IsNullOrWhiteSpace(common))
            {
                return null;
            }

            commonDirectory = PathHygiene.Canonicalize(
                Path.IsPathRooted(common) ? common : Path.Combine(gitDirectory, common));
            if (!Directory.Exists(commonDirectory))
            {
                return null;
            }
        }

        var isWorktree = hasCommonDirectory
            && !string.Equals(gitDirectory, commonDirectory, StringComparison.OrdinalIgnoreCase);
        var isSubmodule = metadataIsPointer
            && !isWorktree
            && !PathHygiene.IsUnder(gitDirectory, Path.Combine(checkout, ".git"));
        var parentPath = isWorktree ? FindMainCheckout(commonDirectory) : null;

        return new RepositoryIdentityInfo(
            gitDirectory,
            commonDirectory,
            isWorktree,
            isSubmodule,
            parentPath);
    }

    private static string? FindMainCheckout(string commonDirectory)
    {
        if (!string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = Directory.GetParent(commonDirectory)?.FullName;
        return parent is not null && PathHygiene.IsRepositoryRoot(parent)
            ? PathHygiene.Canonicalize(parent)
            : null;
    }

    private static string? ReadSingleLine(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxPointerLength)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadLine();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
