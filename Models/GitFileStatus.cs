namespace DevDesk.Models;

public sealed record GitFileStatus(
    string Kind,
    string IndexStatus,
    string WorktreeStatus,
    string Path,
    string? OriginalPath = null);

public sealed record GitRepositoryStatus(
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<GitFileStatus> Files,
    bool IsBareRepository = false);

public sealed record GitBranchInfo(string Name, bool IsCurrent, string? Upstream, bool IsRemote = false);

public sealed record GitConflictState(string? Operation, IReadOnlyList<string> ConflictedPaths)
{
    public bool HasConflict => Operation is not null || ConflictedPaths.Count > 0;
}

public sealed record GitRemoteInfo(string Name, string Url, string Direction);
public sealed record GitTagInfo(string Name, string Date, string Subject);
