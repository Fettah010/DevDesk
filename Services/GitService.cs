using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevDesk.Models;

namespace DevDesk.Services;

public enum GitFailureKind
{
    None,
    Authentication,
    Network,
    Conflict,
    IndexLocked,
    Cancelled,
    TimedOut,
    GitUnavailable,
    Other,
}

public sealed record GitOperationResult(
    bool Succeeded,
    GitFailureKind Failure,
    int ExitCode,
    string Output,
    string Error)
{
    public static GitOperationResult Success(GitResult result) =>
        new(true, GitFailureKind.None, result.ExitCode, result.StdOut, string.Empty);
}

public sealed record GitReadResult<T>(GitOperationResult Operation, T? Value);
/// <summary>Typed, non-throwing boundary for Git operations.</summary>
public sealed class GitService
{
    private const string HistoryFormat = "%H%x1f%s%x1f%an%x1f%aI";
    private const int DiffTextLimit = 100_000;
    public TimeSpan OperationTimeout { get; }
    public TimeSpan NetworkOperationTimeout { get; }

    public GitService(TimeSpan? operationTimeout = null, TimeSpan? networkOperationTimeout = null)
    {
        OperationTimeout = operationTimeout ?? GitRunner.DefaultTimeout;
        NetworkOperationTimeout = networkOperationTimeout ?? TimeSpan.FromMinutes(2);
    }
    private static readonly Regex CredentialPattern = new(
        "(?i)(https?://)[^/@\\s]+@", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Task<GitOperationResult> RunAsync(
        string repositoryPath,
        CancellationToken cancellationToken,
        params string[] arguments) =>
        RunCoreAsync(repositoryPath, OperationTimeout, cancellationToken, arguments);

    private async Task<GitOperationResult> RunCoreAsync(
        string repositoryPath,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string[] arguments)
    {
        try
        {
            var result = await GitRunner.RunAsync(repositoryPath, timeout, cancellationToken, arguments).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return GitOperationResult.Success(result);
            }

            var failure = Classify(result.StdErr);
            return new GitOperationResult(false, failure, result.ExitCode, result.StdOut, Redact(result.StdErr));
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult(false, GitFailureKind.Cancelled, -1, string.Empty, "Git operation cancelled.");
        }
        catch (Exception)
        {
            return new GitOperationResult(false, GitFailureKind.Other, -1, string.Empty, "Git operation could not be completed.");
        }
    }

    private Task<GitOperationResult> RunNetworkAsync(string repositoryPath, CancellationToken cancellationToken, params string[] arguments) =>
        RunCoreAsync(repositoryPath, NetworkOperationTimeout, cancellationToken, arguments);

    public async Task<GitReadResult<GitRepositoryStatus>> GetStatusAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var bare = await RunAsync(repositoryPath, cancellationToken, "rev-parse", "--is-bare-repository").ConfigureAwait(false);
        if (!bare.Succeeded)
            return new GitReadResult<GitRepositoryStatus>(bare, null);
        if (string.Equals(bare.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            var branch = await RunAsync(repositoryPath, cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD").ConfigureAwait(false);
            return new GitReadResult<GitRepositoryStatus>(GitOperationResult.Success(new GitResult(0, string.Empty, string.Empty)),
                new GitRepositoryStatus(branch.Succeeded ? branch.Output.Trim() : null, null, 0, 0, Array.Empty<GitFileStatus>(), true));
        }
        var operation = await RunAsync(repositoryPath, cancellationToken, "status", "--porcelain=v2", "-z", "--branch").ConfigureAwait(false);
        return new GitReadResult<GitRepositoryStatus>(
            operation,
            operation.Succeeded ? GitStatusParser.Parse(operation.Output) : null);
    }

    public async Task<GitReadResult<IReadOnlyList<CommitInfo>>> GetHistoryAsync(
        string repositoryPath,
        int page = 0,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (page < 0 || pageSize is < 1 or > 100)
        {
            var invalid = new GitOperationResult(false, GitFailureKind.Other, -1, string.Empty, "History page must be non-negative and page size must be between 1 and 100.");
            return new GitReadResult<IReadOnlyList<CommitInfo>>(invalid, Array.Empty<CommitInfo>());
        }

        var operation = await RunAsync(repositoryPath, cancellationToken,
            "log", $"--skip={(long)page * pageSize}", $"-{pageSize}", $"--format={HistoryFormat}")
            .ConfigureAwait(false);
        var commits = operation.Succeeded ? ParseHistory(operation.Output) : Array.Empty<CommitInfo>();
        return new GitReadResult<IReadOnlyList<CommitInfo>>(operation, commits);
    }

    public async Task<GitReadResult<IReadOnlyList<GitBranchInfo>>> GetBranchesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var operation = await RunAsync(repositoryPath, cancellationToken,
            "branch", "--all", "--format=%(refname:short)%09%(HEAD)%09%(upstream:short)")
            .ConfigureAwait(false);
        var branches = new List<GitBranchInfo>();
        if (operation.Succeeded)
        {
            foreach (var line in operation.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\t');
                if (fields.Length != 3) continue;
                var remote = fields[0].StartsWith("remotes/", StringComparison.Ordinal);
                branches.Add(new GitBranchInfo(remote ? fields[0][8..] : fields[0], fields[1] == "*", string.IsNullOrEmpty(fields[2]) ? null : fields[2], remote));
            }
        }

        return new GitReadResult<IReadOnlyList<GitBranchInfo>>(operation, branches);
    }

    public async Task<GitReadResult<IReadOnlyList<GitRemoteInfo>>> GetRemotesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var operation = await RunAsync(repositoryPath, cancellationToken, "remote", "-v").ConfigureAwait(false);
        var remotes = new List<GitRemoteInfo>();
        if (operation.Succeeded)
        {
            foreach (var line in operation.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\t', 2);
                if (fields.Length != 2) continue;
                var directionStart = fields[1].LastIndexOf(" (", StringComparison.Ordinal);
                if (directionStart < 0 || !fields[1].EndsWith(')')) continue;
                remotes.Add(new GitRemoteInfo(fields[0], Redact(fields[1][..directionStart]), fields[1][(directionStart + 2)..^1]));
            }
        }
        return new GitReadResult<IReadOnlyList<GitRemoteInfo>>(operation, remotes);
    }

    public async Task<GitReadResult<IReadOnlyList<GitTagInfo>>> GetTagsAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var operation = await RunAsync(repositoryPath, cancellationToken,
            "tag", "--list", "--format=%(refname:short)%09%(creatordate:iso-strict)%09%(subject)")
            .ConfigureAwait(false);
        var tags = new List<GitTagInfo>();
        if (operation.Succeeded)
        {
            foreach (var line in operation.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\t', 3);
                if (fields.Length == 3) tags.Add(new GitTagInfo(fields[0], fields[1], fields[2]));
            }
        }
        return new GitReadResult<IReadOnlyList<GitTagInfo>>(operation, tags);
    }

    public async Task<GitOperationResult> GetCommitDiffstatAsync(string repositoryPath, string commitHash, CancellationToken cancellationToken = default)
    {
        if (!IsCommitHash(commitHash))
            return new GitOperationResult(false, GitFailureKind.Other, -1, string.Empty, "The commit id is invalid.");
        var operation = await RunAsync(repositoryPath, cancellationToken,
            "show", "--stat", "--format=fuller", "--no-renames", "--no-color", "--no-ext-diff", commitHash)
            .ConfigureAwait(false);
        return CapDiff(operation);
    }

    public async Task<GitOperationResult> GetFileDiffAsync(string repositoryPath, string path, bool staged = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split(new[] { '/', '\\' }).Contains("..", StringComparer.Ordinal))
            return new GitOperationResult(false, GitFailureKind.Other, -1, string.Empty, "Select a repository-relative file path.");
        var args = staged
            ? new[] { "diff", "--cached", "--no-ext-diff", "--no-color", "--", path }
            : new[] { "diff", "--no-ext-diff", "--no-color", "--", path };
        return CapDiff(await RunAsync(repositoryPath, cancellationToken, args).ConfigureAwait(false));
    }

    private static GitOperationResult CapDiff(GitOperationResult operation)
    {
        if (!operation.Succeeded || operation.Output.Length <= DiffTextLimit) return operation;
        return operation with { Output = operation.Output[..DiffTextLimit] + "\n… diff truncated at 100,000 characters." };
    }

    private static bool IsCommitHash(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 4 and <= 64 && value.All(Uri.IsHexDigit);

    public async Task<GitReadResult<GitConflictState>> GetConflictStateAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!status.Operation.Succeeded)
            return new GitReadResult<GitConflictState>(status.Operation, null);

        var merge = await RunAsync(repositoryPath, cancellationToken, "rev-parse", "--verify", "--quiet", "MERGE_HEAD").ConfigureAwait(false);
        var rebase = await RunAsync(repositoryPath, cancellationToken, "rev-parse", "--verify", "--quiet", "REBASE_HEAD").ConfigureAwait(false);
        var operation = merge.Succeeded ? "merge" : rebase.Succeeded ? "rebase" : null;
        var paths = status.Value!.Files.Where(file => file.Kind == "conflict").Select(file => file.Path).ToArray();
        return new GitReadResult<GitConflictState>(GitOperationResult.Success(new GitResult(0, string.Empty, string.Empty)),
            new GitConflictState(operation, paths));
    }

    public Task<GitOperationResult> AbortMergeAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryPath, cancellationToken, "merge", "--abort");

    public Task<GitOperationResult> ContinueMergeAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryPath, cancellationToken, "-c", "core.editor=true", "merge", "--continue");

    public Task<GitOperationResult> AbortRebaseAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryPath, cancellationToken, "rebase", "--abort");

    public Task<GitOperationResult> ContinueRebaseAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryPath, cancellationToken, "-c", "core.editor=true", "rebase", "--continue");

    public Task<GitOperationResult> StageAsync(string repositoryPath, IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        RunPathsAsync(repositoryPath, "add", paths, cancellationToken);

    public Task<GitOperationResult> StageAllAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryPath, cancellationToken, "add", "--all");

    public Task<GitOperationResult> UnstageAsync(string repositoryPath, IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        RunPathsAsync(repositoryPath, "restore", "--staged", paths, cancellationToken);

    public Task<GitOperationResult> DiscardTrackedChangesAsync(string repositoryPath, IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        RunPathsAsync(repositoryPath, "restore", new[] { "--source=HEAD", "--staged", "--worktree" }, paths, cancellationToken);

    public Task<GitOperationResult> CheckoutFileAsync(string repositoryPath, IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        RunPathsAsync(repositoryPath, "restore", new[] { "--source=HEAD", "--staged", "--worktree" }, paths, cancellationToken);

    public Task<GitOperationResult> CommitAsync(string repositoryPath, string message, bool amend = false, bool signOff = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Task.FromResult(new GitOperationResult(false, GitFailureKind.Other, -1, string.Empty, "A commit message is required."));
        var args = new List<string> { "commit" };
        if (amend) args.Add("--amend");
        if (signOff) args.Add("--signoff");
        args.Add("-m");
        args.Add(message);
        return RunAsync(repositoryPath, cancellationToken, args.ToArray());
    }

    public Task<GitOperationResult> FetchAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunNetworkAsync(repositoryPath, cancellationToken, "fetch", "origin");

    public Task<GitOperationResult> PullFastForwardOnlyAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunNetworkAsync(repositoryPath, cancellationToken, "pull", "--ff-only");

    public async Task<GitOperationResult> PushAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var branch = await RunAsync(repositoryPath, cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD").ConfigureAwait(false);
        if (!branch.Succeeded)
            return new GitOperationResult(false, GitFailureKind.Other, branch.ExitCode, string.Empty, "Cannot push while HEAD is detached.");
        var upstream = await GetBranchesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!upstream.Operation.Succeeded) return upstream.Operation;
        var current = upstream.Value?.FirstOrDefault(item => item.IsCurrent);
        return current?.Upstream is null
            ? await RunNetworkAsync(repositoryPath, cancellationToken, "push", "--set-upstream", "origin", branch.Output.Trim()).ConfigureAwait(false)
            : await RunNetworkAsync(repositoryPath, cancellationToken, "push", "origin", branch.Output.Trim()).ConfigureAwait(false);
    }

    public Task<GitOperationResult> CreateBranchAsync(string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        RunBranchAsync(repositoryPath, name, cancellationToken, "switch", "-c");

    public Task<GitOperationResult> SwitchBranchAsync(string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        RunBranchAsync(repositoryPath, name, cancellationToken, "switch");

    public Task<GitOperationResult> DeleteMergedBranchAsync(string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        RunBranchAsync(repositoryPath, name, cancellationToken, "branch", "-d");

    public async Task<GitOperationResult> CreateTagAsync(string repositoryPath, string name, string message, CancellationToken cancellationToken = default)
    {
        var valid = await RunAsync(repositoryPath, cancellationToken, "check-ref-format", "refs/tags/" + name).ConfigureAwait(false);
        if (!valid.Succeeded) return new GitOperationResult(false, GitFailureKind.Other, valid.ExitCode, string.Empty, "The tag name is invalid.");
        return await RunAsync(repositoryPath, cancellationToken, "tag", "-a", name, "-m", message).ConfigureAwait(false);
    }

    public async Task<GitOperationResult> DeleteLocalTagAsync(string repositoryPath, string name, CancellationToken cancellationToken = default)
    {
        var valid = await RunAsync(repositoryPath, cancellationToken, "check-ref-format", "refs/tags/" + name).ConfigureAwait(false);
        if (!valid.Succeeded) return new GitOperationResult(false, GitFailureKind.Other, valid.ExitCode, string.Empty, "The tag name is invalid.");
        return await RunAsync(repositoryPath, cancellationToken, "tag", "-d", name).ConfigureAwait(false);
    }

    private Task<GitOperationResult> RunBranchAsync(string repositoryPath, string name, CancellationToken cancellationToken, params string[] prefix)
    {
        var valid = RunAsync(repositoryPath, cancellationToken, "check-ref-format", "--branch", name);
        return RunBranchValidatedAsync(valid, repositoryPath, name, cancellationToken, prefix);
    }

    private async Task<GitOperationResult> RunBranchValidatedAsync(Task<GitOperationResult> validation, string repositoryPath, string name, CancellationToken cancellationToken, string[] prefix)
    {
        var valid = await validation.ConfigureAwait(false);
        if (!valid.Succeeded) return new GitOperationResult(false, GitFailureKind.Other, valid.ExitCode, string.Empty, "The branch name is invalid.");
        return await RunAsync(repositoryPath, cancellationToken, prefix.Concat(new[] { name }).ToArray()).ConfigureAwait(false);
    }

    private Task<GitOperationResult> RunPathsAsync(string repositoryPath, string command, IEnumerable<string> paths, CancellationToken cancellationToken) =>
        RunPathsAsync(repositoryPath, command, Array.Empty<string>(), paths, cancellationToken);

    private Task<GitOperationResult> RunPathsAsync(string repositoryPath, string command, string option, IEnumerable<string> paths, CancellationToken cancellationToken) =>
        RunPathsAsync(repositoryPath, command, new[] { option }, paths, cancellationToken);

    private Task<GitOperationResult> RunPathsAsync(string repositoryPath, string command, IReadOnlyList<string> options, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var list = paths?.ToArray() ?? Array.Empty<string>();
        if (list.Length == 0 || list.Any(path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split(new[] { '/', '\\' }).Contains("..", StringComparer.Ordinal)))
            return Task.FromResult(new GitOperationResult(false, GitFailureKind.Other, -1, string.Empty, "Select one or more repository-relative paths."));
        return RunAsync(repositoryPath, cancellationToken, new[] { command }.Concat(options).Concat(new[] { "--" }).Concat(list).ToArray());
    }

    public static IReadOnlyList<CommitInfo> ParseHistory(string output)
    {
        var commits = new List<CommitInfo>();
        foreach (var record in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\u001f');
            if (fields.Length != 4) continue;
            commits.Add(new CommitInfo
            {
                Hash = fields[0],
                Subject = fields[1],
                Author = fields[2],
                Date = fields[3],
            });
        }

        return commits;
    }

    public static GitFailureKind Classify(string error)
    {
        var text = error ?? string.Empty;
        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return GitFailureKind.TimedOut;
        if (text.Contains("not available on PATH", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not be started", StringComparison.OrdinalIgnoreCase)) return GitFailureKind.GitUnavailable;
        if (text.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)) return GitFailureKind.Authentication;
        if (text.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase)) return GitFailureKind.Conflict;
        if (text.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable to create", StringComparison.OrdinalIgnoreCase) && text.Contains(".lock", StringComparison.OrdinalIgnoreCase)) return GitFailureKind.IndexLocked;
        if (text.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable to access", StringComparison.OrdinalIgnoreCase)) return GitFailureKind.Network;
        return GitFailureKind.Other;
    }

    public static string Redact(string text) => CredentialPattern.Replace(text ?? string.Empty, "$1[redacted]@");
}
