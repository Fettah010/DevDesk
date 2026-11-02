using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevDesk.Services;

public sealed record ReleaseNoteCommit(string Subject, string Hash, string? PullRequestUrl = null);

public static partial class ReleaseNotesGenerator
{
    private static readonly (string Heading, string Type)[] Sections =
    [
        ("Features", "feat"),
        ("Fixes", "fix"),
        ("Documentation", "docs"),
        ("Maintenance", "chore"),
        ("Other changes", "")
    ];

    public static string Generate(IEnumerable<ReleaseNoteCommit> commits)
    {
        ArgumentNullException.ThrowIfNull(commits);

        var groups = Sections.ToDictionary(section => section.Type, _ => new List<string>());
        foreach (var commit in commits)
        {
            ArgumentNullException.ThrowIfNull(commit);
            var subject = NormalizeSubject(commit.Subject);
            if (subject.Length == 0)
            {
                continue;
            }

            var type = Classify(subject);
            var entry = new StringBuilder("- ").Append(EscapeMarkdown(subject));
            if (IsCommitHash(commit.Hash))
            {
                entry.Append(" (`").Append(commit.Hash[..7]).Append('`');
                var pullRequestNumber = GetPullRequestNumber(commit.PullRequestUrl);
                if (pullRequestNumber is not null)
                {
                    entry.Append(", [#").Append(pullRequestNumber)
                        .Append("](").Append(commit.PullRequestUrl).Append(')');
                }
                entry.Append(')');
            }

            groups[type].Add(entry.ToString());
        }

        var notes = new StringBuilder();
        foreach (var section in Sections)
        {
            var entries = groups[section.Type];
            if (entries.Count == 0)
            {
                continue;
            }

            if (notes.Length > 0)
            {
                notes.AppendLine().AppendLine();
            }

            notes.Append("## ").AppendLine(section.Heading);
            foreach (var entry in entries)
            {
                notes.AppendLine(entry);
            }
            notes.Length -= Environment.NewLine.Length;
        }

        return notes.ToString();
    }

    private static string Classify(string subject)
    {
        var match = ConventionalCommitPrefixRegex().Match(subject);
        if (!match.Success)
        {
            return string.Empty;
        }

        var type = match.Groups["type"].Value;
        return Sections.Any(section => section.Type == type) ? type : string.Empty;
    }

    private static string NormalizeSubject(string? subject) =>
        string.Join(' ', (subject ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string EscapeMarkdown(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '[' or ']' or '*' or '_' or '#' or '`' or '!' or '>' or '|')
            {
                escaped.Append('\\');
            }
            escaped.Append(character);
        }

        return escaped.ToString();
    }

    private static bool IsCommitHash(string? value) =>
        value is { Length: >= 7 and <= 40 } && value.All(Uri.IsHexDigit);

    private static string? GetPullRequestNumber(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            return null;
        }

        var match = PullRequestPathRegex().Match(uri.AbsolutePath);
        return match.Success ? match.Groups["number"].Value : null;
    }

    [GeneratedRegex(@"^(?<type>[a-z]+)(?:\([^)]+\))?!?:\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ConventionalCommitPrefixRegex();

    [GeneratedRegex(@"^/[^/]+/[^/]+/pull/(?<number>[1-9][0-9]*)/?$", RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestPathRegex();
}
