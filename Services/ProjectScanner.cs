using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>Outcome of a scan: the project list and the self repo as its own workspace.</summary>
public sealed class ScannerResult
{
    public ProjectModel? Self { get; init; }
    public IReadOnlyList<ProjectModel> Projects { get; init; } = Array.Empty<ProjectModel>();
}

/// <summary>
/// Phase 0 ProjectScanner. Looks inside <see cref="DefaultScanRoot"/> (the
/// DevEcosystem folder), finds git repos / project-marked folders, and reads
/// read-only git state + csproj versions per the scan model in AGENTS.md:
///
///  * Scan root = DevEcosystem, depth 2.
///  * DevDesk itself is never a scanned project — it becomes the self
///    workspace (<see cref="ScannerResult.Self"/>).
///  * Stop descending once a repo is found (its contents are not more projects).
///  * Hygiene: skip bin/obj/.git/.vs/node_modules/Releases/Logs/….
///  * Local-only: no network calls, no writes — a broken git never crashes a scan.
/// </summary>
public sealed class ProjectScanner
{
    public const string DefaultScanRoot = @"C:\Code\projects\DevEcosystem";
    public const int DefaultMaxDepth = 2;

    private const string SelfRepoName = "DevDesk";
    private const int CommitCount = 3;

    private static readonly Regex VersionTagRegex = new(
        "<(Version|AssemblyVersion|FileVersion)>\\s*(?<value>[^<]+?)\\s*</\\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BehindRegex = new("behind\\s+(\\d+)", RegexOptions.Compiled);
    private static readonly Regex NoCommitsRegex = new("^##\\s+No commits yet", RegexOptions.Compiled);

    private static readonly string[] IgnoredNames =
    {
        "bin", "obj", ".vs", ".vscode", ".git", "node_modules", "packages",
        "Releases", "ReleasesLocal", "Logs", "dist", "build", "_ReSharper",
    };

    private static readonly string[] MarkerExtensions =
    {
        ".sln", ".slnx", ".csproj", ".vcxproj", ".fsproj", ".vbproj",
    };

    private readonly string _root;
    private readonly string _selfPath;
    private readonly int _maxDepth;

    public ProjectScanner(string? root = null, string? selfPath = null, int maxDepth = DefaultMaxDepth)
    {
        _root = root ?? DefaultScanRoot;
        _selfPath = (selfPath ?? Path.Combine(_root, SelfRepoName)).TrimEnd('\\', '/');
        _maxDepth = Math.Max(1, maxDepth);
    }

    public string ScanRoot => _root;
    public string SelfPath => _selfPath;

    public async Task<ScannerResult> ScanAsync()
    {
        var projects = new List<ProjectModel>();

        if (Directory.Exists(_root))
        {
            await WalkAsync(_root, 1, projects).ConfigureAwait(false);
        }

        var self = Directory.Exists(_selfPath) && IsRepositoryRoot(_selfPath)
            ? await BuildModelAsync(_selfPath, forceWorkspace: true).ConfigureAwait(false)
            : (ProjectModel?)null;

        return new ScannerResult { Self = self, Projects = projects };
    }

    private async Task WalkAsync(string dir, int depth, List<ProjectModel> projects)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);
            if (name is null || IsIgnored(name) || name.Length > 0 && name[0] == '.')
            {
                continue;
            }

            if (IsSelfPath(child))
            {
                continue;
            }

            if (IsRepositoryRoot(child) || HasProjectMarker(child))
            {
                var model = await BuildModelAsync(child, forceWorkspace: false).ConfigureAwait(false);
                if (model is not null)
                {
                    projects.Add(model);
                }
            }
            else if (depth < _maxDepth)
            {
                await WalkAsync(child, depth + 1, projects).ConfigureAwait(false);
            }
        }
    }

    private static bool IsIgnored(string name) => IgnoredNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private bool IsSelfPath(string path) =>
        string.Equals(path.TrimEnd('\\', '/'), _selfPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsRepositoryRoot(string dir) => Directory.Exists(Path.Combine(dir, ".git"));

    private static bool HasProjectMarker(string dir)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return files.Any(f => MarkerExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<ProjectModel?> BuildModelAsync(string repoPath, bool forceWorkspace)
    {
        var name = Path.GetFileName(repoPath.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var status = await GitRunner.RunAsync(repoPath, "status", "--porcelain", "--branch").ConfigureAwait(false);
        var remoteVerbose = await GitRunner.RunAsync(repoPath, "remote", "--verbose").ConfigureAwait(false);
        var log = status.ExitCode == 0
            ? await GitRunner.RunAsync(repoPath, "log", $"-{CommitCount}", "--pretty=format:%h%x09%s%x09%an%x09%ar").ConfigureAwait(false)
            : status;

        var (branch, dirty, behind) = await ParseStatusAsync(status, repoPath).ConfigureAwait(false);
        var (hasRemote, remoteOk, remoteHost) = ParseRemotes(remoteVerbose.StdOut);
        var commits = ParseCommits(log.StdOut);
        var (version, versionMismatch) = ReadVersion(repoPath);
        var isWorkspace = forceWorkspace || CountSubfolderProjects(repoPath) >= 2;

        return new ProjectModel
        {
            Name = name,
            Path = repoPath,
            IconGlyph = "\uE8B7",
            IsWorkspace = isWorkspace,
            Branch = branch,
            Version = version,
            VersionMismatch = versionMismatch,
            HasRemote = hasRemote,
            RemoteOk = remoteOk,
            RemoteHost = remoteHost,
            Dirty = dirty,
            BehindUpstream = behind,
            StatusText = StatusTextFor(hasRemote, dirty, behind),
            RecentCommits = commits,
            Components = forceWorkspace ? BuildComponents(repoPath, name) : Array.Empty<ComponentInfo>(),
        };
    }

    private static async Task<(string Branch, bool Dirty, bool Behind)> ParseStatusAsync(GitResult status, string repoPath)
    {
        var lines = status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dirty = lines.Any(l => !l.StartsWith("##"));
        var behind = lines.Any(l => BehindRegex.IsMatch(l));

        var branch = ParseBriefBranch(lines.FirstOrDefault(l => l.StartsWith("##")));
        if (string.IsNullOrEmpty(branch))
        {
            // Unborn branch (no commits yet) or missing brief line → ask git.
            var symbolic = await GitRunner.RunAsync(repoPath, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false);
            branch = symbolic.ExitCode == 0 && !string.IsNullOrWhiteSpace(symbolic.StdOut)
                ? symbolic.StdOut.Trim()
                : "detached";
        }

        return (branch, dirty, behind);
    }

    private static string ParseBriefBranch(string? brief)
    {
        if (string.IsNullOrEmpty(brief) || NoCommitsRegex.IsMatch(brief))
        {
            return string.Empty;
        }

        var token = brief[2..].Trim();
        if (token.StartsWith("HEAD (", StringComparison.Ordinal))
        {
            return "detached";
        }

        var dots = token.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0)
        {
            token = token[..dots];
        }

        var bracket = token.IndexOf('[');
        if (bracket >= 0)
        {
            token = token[..bracket];
        }

        return token.Trim();
    }

    private static (bool HasRemote, bool RemoteOk, string RemoteHost) ParseRemotes(string stdout)
    {
        var urls = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 1)
            .Select(parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (urls.Length == 0)
        {
            return (false, false, "Local only");
        }

        return (true, true, urls.Any(IsGitHubUrl) ? "GitHub" : "Other");
    }

    private static bool IsGitHubUrl(string url) =>
        url.Contains("github.com", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("git@github", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<CommitInfo> ParseCommits(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return Array.Empty<CommitInfo>();
        }

        var commits = new List<CommitInfo>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
            {
                continue;
            }

            commits.Add(new CommitInfo
            {
                Hash = parts[0],
                Subject = parts[1],
                Author = parts[2],
                Date = parts[3],
            });
        }

        return commits;
    }

    private static (string Version, bool Mismatch) ReadVersion(string repoPath)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(repoPath, "*.csproj");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("—", false);
        }

        var csproj = files.FirstOrDefault();
        if (csproj is null)
        {
            return ("—", false);
        }

        string content;
        try
        {
            content = File.ReadAllText(csproj);
        }
        catch
        {
            return ("—", false);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in VersionTagRegex.Matches(content))
        {
            values[match.Groups[1].Value] = match.Groups["value"].Value.Trim();
        }

        if (!values.TryGetValue("Version", out var version))
        {
            return ("—", false);
        }

        var mismatch = values.Any(kv =>
            !string.Equals(kv.Key, "Version", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kv.Value, version, StringComparison.Ordinal));

        return (version, mismatch);
    }

    private static int CountSubfolderProjects(string repoPath) => CountUnder(repoPath);

    private static int CountUnder(string dir)
    {
        var count = 0;
        foreach (var sub in SafeGetDirectories(dir))
        {
            count += SafeGetFiles(sub).Count(f =>
                MarkerExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
            count += CountUnder(sub);
        }

        return count;
    }

    private static string[] SafeGetDirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string[] SafeGetFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<ComponentInfo> BuildComponents(string repoPath, string repoName)
    {
        var components = new List<ComponentInfo>();

        var csproj = SafeGetFiles(repoPath).FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (csproj is not null)
        {
            components.Add(new ComponentInfo
            {
                Name = $"{repoName} app",
                RelativePath = $@"{repoName}\{Path.GetFileName(csproj)}",
                Kind = "app",
            });
        }

        foreach (var dir in SafeGetDirectories(repoPath))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName is null)
            {
                continue;
            }

            switch (dirName.ToLowerInvariant())
            {
                case "devdesk.tests":
                    components.Add(new ComponentInfo { Name = dirName, RelativePath = $@"{repoName}\{dirName}", Kind = "tests" });
                    break;
                case "docs":
                    components.Add(new ComponentInfo { Name = "Docs", RelativePath = $@"{repoName}\docs", Kind = "docs" });
                    break;
                case "scripts":
                    components.Add(new ComponentInfo { Name = "Scripts", RelativePath = $@"{repoName}\Scripts", Kind = "scripts" });
                    break;
                case ".github":
                    components.Add(new ComponentInfo { Name = "CI workflow", RelativePath = $@"{repoName}\.github\workflows", Kind = "workflow" });
                    break;
            }
        }

        return components;
    }

    private static string StatusTextFor(bool hasRemote, bool dirty, bool behind)
    {
        if (!hasRemote)
        {
            return "No remote configured";
        }

        if (dirty)
        {
            return "Uncommitted changes";
        }

        return behind ? "Behind upstream" : "Clean working tree";
    }
}