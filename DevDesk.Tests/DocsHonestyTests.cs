using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// v0.1.0's promise is that nothing on screen implies a capability that does not
/// exist. That is a property of text and markup, so it is checked as one: no
/// "Phase 1/2" copy, no reference to the deleted plan file, no invented rows, and
/// every version quoted next to a disabled control must really be on the roadmap.
/// <para>
/// These are source scans on purpose. A UI test could only inspect the pages it
/// happened to visit, and the page nobody visits is exactly where stale copy
/// survives.
/// </para>
/// </summary>
public class DocsHonestyTests
{
    /// <summary>The vocabulary this version removes (work order §W-0.1.10).</summary>
    private static readonly string[] PhaseCopy =
    {
        "Phase 0", "Phase 1", "Phase 2", "Phase 3",
        "not wired", "coming soon",
    };

    /// <summary>Literals that were hardcoded sample data and must stay gone.</summary>
    private static readonly string[] SampleRows =
    {
        "run #47",
        "Delta 2.1 MB",
        "Build &amp; publish",
        "pi 3 lines",
        "2m 10s",
        "1m 58s",
        "v0.0.2",
        "3 w ago",
        // Diagnostics used to show nine invented log lines.
        "Sample log line",
        "Project scan started",
        "Loaded 3 projects",
    };

    private static readonly string[] TextFiles =
    {
        "README.md", "AGENTS.md",
    };

    [Fact]
    public void Docs_NoPhaseCopy()
    {
        var offenders = new List<string>();

        foreach (var file in PageFiles().Concat(TextFilesForRoot()))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(RepoLayout.Root, file);

            foreach (var banned in PhaseCopy)
            {
                var index = text.IndexOf(banned, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                var line = 1 + text.Take(index).Count(c => c == '\n');
                offenders.Add($"{relative}:{line}: '{banned}'");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Copy that promises a capability without naming its version:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Docs_PlanFileExists()
    {
        var roadmap = RepoLayout.Path("docs", "DEVDESK-ROADMAP.md");
        Assert.True(File.Exists(roadmap), "docs/DEVDESK-ROADMAP.md is missing — the DevDesk page links to it.");

        var workOrder = RepoLayout.Path("docs", "versions", "V0.1.0.md");
        Assert.True(File.Exists(workOrder), "docs/versions/V0.1.0.md is missing.");

        // The retired plan may only be mentioned as history, inside docs/. The
        // name is assembled at runtime so this guard does not flag itself — the
        // same trick RepoLayout uses for the template repo name.
        var retiredPlan = "DEVDESK-" + "PLAN.md";

        var offenders = SourceAndCodeFiles()
            .Where(file => !Path.GetRelativePath(RepoLayout.Root, file)
                .StartsWith("docs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains(retiredPlan, StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(RepoLayout.Root, file))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Code still points at the deleted docs/" + retiredPlan + ":"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Pages_NoHardcodedSampleRows()
    {
        var offenders = new List<string>();

        foreach (var file in PageFiles())
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(RepoLayout.Root, file);

            foreach (var literal in SampleRows)
            {
                if (text.Contains(literal, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{relative}: '{literal}'");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Invented data is back on a page (a row nobody can tell apart from a real read):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Pages_DisabledReasonsNameVersionsOnTheRoadmap()
    {
        // "Arrives in v0.4.0" is only honest if 0.4.0 exists in the plan. The
        // roadmap is the source of truth, so every version the UI quotes has to
        // appear in it.
        var roadmap = File.ReadAllText(RepoLayout.Path("docs", "DEVDESK-ROADMAP.md"));
        var version = new Regex(@"\bv(\d+\.\d+\.\d+)\b", RegexOptions.Compiled);

        var quoted = new List<(string File, int Line, string Version)>();

        foreach (var file in PageFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in version.Matches(lines[index]))
                {
                    quoted.Add((
                        Path.GetRelativePath(RepoLayout.Root, file),
                        index + 1,
                        match.Groups[1].Value));
                }
            }
        }

        var unknown = quoted
            .Where(q => !roadmap.Contains(q.Version, StringComparison.Ordinal))
            .Select(q => $"{q.File}:{q.Line}: v{q.Version}")
            .Distinct()
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "A page promises a version the roadmap does not have:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unknown));
    }

    /// <summary>
    /// The status line is the first thing a reader sees, and it went stale for
    /// three releases while the work moved on. Whatever version it names must
    /// exist in the roadmap and must be the newest work order in
    /// <c>docs/versions</c>, so the copy cannot drift silently again.
    /// </summary>
    [Fact]
    public void Docs_StatusLineNamesTheNewestVersionOnTheRoadmap()
    {
        var roadmap = File.ReadAllText(RepoLayout.Path("docs", "DEVDESK-ROADMAP.md"));
        var statusVersion = new Regex(
            @"(?:Status:|Current status:)[^.]*?\bv(\d+\.\d+\.\d+)\b",
            RegexOptions.Compiled);
        var workOrders = new Regex(@"^V(\d+\.\d+\.\d+)\.md$", RegexOptions.Compiled);
        var newest = Directory
            .EnumerateFiles(RepoLayout.Path("docs", "versions"), "V*.md")
            .Select(file => workOrders.Match(Path.GetFileName(file)))
            .Where(match => match.Success)
            .Select(match => Version.Parse(match.Groups[1].Value))
            .Max();
        var offenders = new List<string>();

        foreach (var name in TextFiles)
        {
            var file = RepoLayout.Path(name);
            if (!File.Exists(file))
            {
                continue;
            }

            var match = statusVersion.Match(File.ReadAllText(file));
            if (!match.Success)
            {
                offenders.Add($"{name}: no status line naming a version");
                continue;
            }

            var quoted = match.Groups[1].Value;
            if (!roadmap.Contains(quoted, StringComparison.Ordinal))
            {
                offenders.Add($"{name}: status names v{quoted}, which the roadmap does not have");
            }
            else if (Version.Parse(quoted) != newest)
            {
                offenders.Add(
                    $"{name}: status names v{quoted}, but the newest work order is v{newest}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Status copy and the roadmap disagree:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Every page's markup and code-behind.</summary>
    private static IEnumerable<string> PageFiles()
    {
        var pages = RepoLayout.Path("Pages");
        foreach (var file in Directory.EnumerateFiles(pages, "*.xaml"))
        {
            yield return file;
        }

        foreach (var file in Directory.EnumerateFiles(pages, "*.xaml.cs"))
        {
            yield return file;
        }
    }

    private static IEnumerable<string> TextFilesForRoot() =>        TextFiles.Select(name => RepoLayout.Path(name)).Where(File.Exists);

    /// <summary>App code and scripts: everything a link or a path could hide in.</summary>
    private static IEnumerable<string> SourceAndCodeFiles()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var skipped = new[] { "bin", "obj", ".git", ".vs", "Releases", "Logs" };

        foreach (var pattern in new[] { "*.cs", "*.xaml", "*.csproj", "*.ps1", "*.yml", "*.md" })
        {
            foreach (var file in Directory.EnumerateFiles(RepoLayout.Root, pattern, options))
            {
                var relative = Path.GetRelativePath(RepoLayout.Root, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(skipped.Contains))
                {
                    continue;
                }

                yield return file;
            }
        }
    }
}
