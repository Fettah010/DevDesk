using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

public sealed record ReleaseNotesDraft(
    string? PreviousTag,
    IReadOnlyList<ReleaseNoteCommit> Commits,
    string Markdown,
    bool IsTruncated);

public sealed record ReleaseNotesDraftResult(
    bool Succeeded,
    ReleaseNotesDraft? Draft,
    string Error);

public sealed class ReleaseNotesDraftService
{
    public const int MaximumCommits = 500;
    private const char FieldSeparator = '\u001f';

    private readonly GitService _git;

    public ReleaseNotesDraftService(GitService git) =>
        _git = git ?? throw new ArgumentNullException(nameof(git));

    public async Task<ReleaseNotesDraftResult> CreateAsync(
        string repositoryPath,
        string? previousTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        if (!string.IsNullOrWhiteSpace(previousTag))
        {
            var validTag = await _git.RunAsync(
                repositoryPath,
                cancellationToken,
                "check-ref-format",
                "refs/tags/" + previousTag).ConfigureAwait(false);
            if (!validTag.Succeeded)
            {
                return Failure("The previous release tag is invalid.");
            }

            var existingTag = await _git.RunAsync(
                repositoryPath,
                cancellationToken,
                "show-ref",
                "--verify",
                "--quiet",
                "refs/tags/" + previousTag).ConfigureAwait(false);
            if (!existingTag.Succeeded)
            {
                return Failure("The selected previous release tag does not exist in this local repository.");
            }
        }

        var revisionRange = string.IsNullOrWhiteSpace(previousTag)
            ? "HEAD"
            : $"refs/tags/{previousTag}..HEAD";
        var log = await _git.RunAsync(
            repositoryPath,
            cancellationToken,
            "log",
            "--no-color",
            "--no-decorate",
            "--no-show-signature",
            $"-n{MaximumCommits + 1}",
            $"--format=%H%x1f%s",
            revisionRange).ConfigureAwait(false);
        if (!log.Succeeded)
        {
            return Failure("Could not read local commits for the release notes.", log);
        }

        var commits = ParseCommits(log.Output);
        var truncated = commits.Count > MaximumCommits;
        if (truncated)
        {
            commits.RemoveRange(MaximumCommits, commits.Count - MaximumCommits);
        }

        var readOnlyCommits = commits.AsReadOnly();
        return new ReleaseNotesDraftResult(
            true,
            new ReleaseNotesDraft(
                string.IsNullOrWhiteSpace(previousTag) ? null : previousTag,
                readOnlyCommits,
                ReleaseNotesGenerator.Generate(readOnlyCommits),
                truncated),
            string.Empty);
    }

    private static List<ReleaseNoteCommit> ParseCommits(string output)
    {
        var commits = new List<ReleaseNoteCommit>();
        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(FieldSeparator, 2);
            if (fields.Length != 2
                || fields[0].Length is < 7 or > 40
                || !fields[0].All(Uri.IsHexDigit))
            {
                continue;
            }

            commits.Add(new ReleaseNoteCommit(fields[1], fields[0]));
        }

        return commits;
    }

    private static ReleaseNotesDraftResult Failure(
        string message,
        GitOperationResult? operation = null) =>
        new(false, null, operation is { Succeeded: false } ? $"{message} {operation.Error}" : message);
}
