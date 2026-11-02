using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleaseNotesGeneratorTests
{
    [Fact]
    public void Generate_GroupsKnownConventionalCommitTypesInStableOrder()
    {
        var notes = ReleaseNotesGenerator.Generate(
        [
            new("misc update", "abcdef0123456789"),
            new("chore: update dependencies", "1234567890abcdef"),
            new("docs: explain release flow", "234567890abcdef1"),
            new("fix(parser): handle empty input", "34567890abcdef12"),
            new("feat: add release preview", "4567890abcdef123")
        ]);

        Assert.True(notes.IndexOf("## Features", StringComparison.Ordinal)
            < notes.IndexOf("## Fixes", StringComparison.Ordinal));
        Assert.True(notes.IndexOf("## Fixes", StringComparison.Ordinal)
            < notes.IndexOf("## Documentation", StringComparison.Ordinal));
        Assert.True(notes.IndexOf("## Documentation", StringComparison.Ordinal)
            < notes.IndexOf("## Maintenance", StringComparison.Ordinal));
        Assert.True(notes.IndexOf("## Maintenance", StringComparison.Ordinal)
            < notes.IndexOf("## Other changes", StringComparison.Ordinal));
        Assert.Contains("- feat: add release preview (`4567890`)", notes);
        Assert.Contains("- fix(parser): handle empty input (`3456789`)", notes);
    }

    [Fact]
    public void Generate_NormalizesMultilineSubjectsAndEscapesMarkdown()
    {
        var notes = ReleaseNotesGenerator.Generate(
        [
            new("feat: add [release]\r\n\npreview *safely*", "abcdef0123456789")
        ]);

        Assert.Contains("- feat: add \\[release\\] preview \\*safely\\* (`abcdef0`)", notes);
        Assert.DoesNotContain("feat: add \\[release\\]\r\n", notes);
    }

    [Fact]
    public void Generate_AddsOnlyValidatedGitHubPullRequestLinks()
    {
        var notes = ReleaseNotesGenerator.Generate(
        [
            new("fix: safe link", "abcdef0123456789", "https://github.com/Fettah010/DevDesk/pull/42"),
            new("fix: wrong host", "bcdef0123456789a", "https://github.com.evil.example/Fettah010/DevDesk/pull/9"),
            new("fix: wrong scheme", "cdef0123456789ab", "http://github.com/Fettah010/DevDesk/pull/7"),
            new("fix: wrong path", "def0123456789abc", "https://github.com/Fettah010/DevDesk/issues/42"),
            new("fix: user info", "ef0123456789abcd", "https://attacker@github.com/Fettah010/DevDesk/pull/3"),
            new("fix: query", "f0123456789abcde", "https://github.com/Fettah010/DevDesk/pull/2?redirect=example")
        ]);

        Assert.Contains("[#42](https://github.com/Fettah010/DevDesk/pull/42)", notes);
        Assert.DoesNotContain("#9", notes);
        Assert.DoesNotContain("#7", notes);
        Assert.DoesNotContain("#3", notes);
        Assert.DoesNotContain("#2", notes);
    }

    [Fact]
    public void Generate_ReturnsEmptyForNoUsableCommits()
    {
        var notes = ReleaseNotesGenerator.Generate(
        [
            new("", "abcdef0123456789"),
            new(" \r\n ", "bcdef0123456789a")
        ]);

        Assert.Empty(notes);
    }
}
