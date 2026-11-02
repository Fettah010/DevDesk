using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>Outcome of a scan: the project list, the self repo as its own workspace,
/// and the template repositories discovered by marker.</summary>
public sealed class ScannerResult
{
    public ProjectModel? Self { get; init; }

    public IReadOnlyList<ProjectModel> Projects { get; init; } = Array.Empty<ProjectModel>();

    /// <summary>Template repositories found during the same walk (read-only data).</summary>
    public IReadOnlyList<TemplateDescriptor> Templates { get; init; } = Array.Empty<TemplateDescriptor>();
}

/// <summary>
/// Contract the catalog depends on, so tests can inject a fake scan without
/// touching git or the file system.
/// </summary>
public interface IScanEngine
{
    string ScanRoot { get; }

    Task<ScannerResult> ScanAsync(
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null);
}

/// <summary>What <c>git status --porcelain=v2 --branch</c> told us.</summary>
public readonly record struct StatusInfo(
    string Branch,
    bool Detached,
    bool Unborn,
    int Ahead,
    int Behind,
    bool Dirty);

/// <summary>
/// Walks <see cref="DefaultScanRoot"/> (the DevEcosystem folder), finds git
/// repos / project-marked folders, and reads read-only git state + csproj
/// versions per the scan model in AGENTS.md:
///
///  * Scan root = DevEcosystem, depth 2.
///  * DevDesk itself is never a scanned project — it becomes the self
///    workspace (<see cref="ScannerResult.Self"/>), located by
///    <see cref="SelfLocator"/>, never by folder name.
///  * Stop descending once a repo is found (its contents are not more projects).
///  * Hygiene: <see cref="PathHygiene"/> decides what is skipped.
///  * Local-only: no network calls, no writes (no locks either), a broken git
///    never crashes a scan, and a cancelled scan stops promptly.
/// </summary>
public sealed class ScanEngine : IScanEngine
{
    public const string DefaultScanRoot = @"C:\Code\projects\DevEcosystem";
    public const int DefaultMaxDepth = 2;

    private const int CommitCount = 3;
    private const int MaxMarkerSearchDepth = 4;
    private const int MaxMarkerFiles = 200;

    /// <summary>Global git options on every call: no pager, no colour, no escaping, no locks.</summary>
    private static readonly string[] GitPrelude =
    {
        "--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", "--no-optional-locks",
    };

    private static readonly Regex VersionTagRegex = new(
        "<(Version|AssemblyVersion|FileVersion)>\\s*(?<value>[^<]+?)\\s*</\\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] MarkerExtensions =
    {
        ".sln", ".slnx", ".csproj", ".vcxproj", ".fsproj", ".vbproj",
    };

    // Template marker files that identify a template repository
    private static readonly string[] TemplateMarkers =
    {
        "App.xaml",
        @"Assets\app.ico",
        @"Scripts\build-and-release.ps1",
    };

    private readonly string _root;
    private readonly string? _selfPath;
    private readonly int _maxDepth;
    private readonly int _maxParallelism;

    /// <param name="root">Folder to walk; defaults to <see cref="DefaultScanRoot"/>.</param>
    /// <param name="selfPath">
    /// Explicit self checkout (tests). When null the checkout is discovered from
    /// the running executable, so a renamed or moved repo still works and an
    /// installed build reports no self at all.
    /// </param>
    /// <param name="maxDepth">How deep a candidate may sit below the root.</param>
    /// <param name="maxParallelism">Repos read at once; defaults to min(4, CPUs).</param>
    public ScanEngine(
        string? root = null,
        string? selfPath = null,
        int maxDepth = DefaultMaxDepth,
        int? maxParallelism = null)
    {
        _root = root ?? DefaultScanRoot;
        _selfPath = NormalizeSelf(selfPath ?? SelfLocator.FindSourceRepo(AppContext.BaseDirectory));
        _maxDepth = Math.Max(1, maxDepth);
        _maxParallelism = Math.Max(1, maxParallelism ?? Math.Min(4, Environment.ProcessorCount));
    }

    public string ScanRoot => _root;

    public string? SelfPath => _selfPath;

    public async Task<ScannerResult> ScanAsync(
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        var candidates = new List<string>();
        var templateCandidates = new List<string>();
        if (Directory.Exists(_root))
        {
            Collect(_root, 1, candidates, templateCandidates, cancellationToken, progress);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The self checkout is read alongside the others: it is three git calls
        // that would otherwise sit on the critical path.
        var selfTask = _selfPath is not null && PathHygiene.IsRepositoryRoot(_selfPath)
            ? BuildModelAsync(_selfPath, forceWorkspace: true, cancellationToken)
            : Task.FromResult<ProjectModel?>(null);

        // Template descriptors are cheap: read the csproj once each, no git.
        var templates = new TemplateDescriptor[templateCandidates.Count];
        for (var i = 0; i < templates.Length; i++)
        {
            templates[i] = ReadTemplateDescriptor(templateCandidates[i]);
        }

        var models = new ProjectModel?[candidates.Count];
        var completed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxParallelism,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            options,
            async (index, token) =>
            {
                var model = await BuildModelAsync(candidates[index], forceWorkspace: false, token)
                    .ConfigureAwait(false);

                models[index] = model;
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ScanProgress(
                    done,
                    candidates.Count,
                    model?.Name ?? Path.GetFileName(candidates[index])));
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var self = await selfTask.ConfigureAwait(false);

        // The array is indexed by candidate order, so the result is stable even
        // though the repos are read concurrently.
        var projects = models.Where(m => m is not null).Select(m => m!).ToList();
        return new ScannerResult { Self = self, Projects = projects, Templates = templates };
    }

    /// <summary>
    /// Collects candidate folders without reading any git state. Discovery is
    /// ordered, so the same disk always produces the same card order.
    /// Also collects template candidates that match the template marker pattern.
    /// </summary>
    private void Collect(
        string directory,
        int depth,
        List<string> candidates,
        List<string> templateCandidates,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress)
    {
        foreach (var child in PathHygiene.SafeEnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(child);
            if (PathHygiene.IsIgnoredFolderName(name) || PathHygiene.IsReparsePoint(child) || IsSelf(child))
            {
                continue;
            }

            if (PathHygiene.IsRepositoryRoot(child) || HasProjectMarker(child))
            {
                candidates.Add(child);
                progress?.Report(new ScanProgress(0, 0, name));
                continue;
            }

            if (HasTemplateMarker(child))
            {
                templateCandidates.Add(child);
                progress?.Report(new ScanProgress(0, 0, name + " (template)"));
                continue;
            }

            if (depth < _maxDepth)
            {
                Collect(child, depth + 1, candidates, templateCandidates, cancellationToken, progress);
            }
        }
    }

    private static string? NormalizeSelf(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : PathHygiene.Canonicalize(path);

    private bool IsSelf(string path) =>
        _selfPath is not null
        && string.Equals(PathHygiene.Canonicalize(path), _selfPath, StringComparison.OrdinalIgnoreCase);

    private static bool HasProjectMarker(string directory) =>
        PathHygiene.SafeEnumerateFiles(directory).Any(IsMarker);

    private static bool IsMarker(string file) =>
        MarkerExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>A folder is a template when it carries a project marker and the
    /// template marker files (App.xaml, Assets/app.ico, Scripts/build-and-release.ps1).</summary>
    private static bool HasTemplateMarker(string directory) =>
        HasProjectMarker(directory) && TemplateMarkers.All(marker =>
            File.Exists(Path.Combine(directory, marker)));

    /// <summary>Reads a template's identity from its csproj: Name (AssemblyName
    /// or folder name), Description, and Version. Never throws — a bad csproj
    /// yields a safe fallback rather than crashing the scan.</summary>
    private static TemplateDescriptor ReadTemplateDescriptor(string templatePath)
    {
        var folderName = Path.GetFileName(templatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        string? assemblyName = null;
        string? description = null;
        string? version = null;

        var projectFile = PathHygiene.SafeEnumerateFiles(templatePath)
            .FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

        if (projectFile is not null)
        {
            string content;
            try
            {
                content = File.ReadAllText(projectFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TemplateDescriptor
                {
                    Name = folderName,
                    Description = string.Empty,
                    Version = "—",
                    FullPath = templatePath,
                };
            }

            // Read with explicit captures for clarity.
            assemblyName = CaptureCsproj(content, "AssemblyName") ?? assemblyName;
            description = CaptureCsproj(content, "Description") ?? description;
            version = CaptureCsproj(content, "Version") ?? version;
        }

        return new TemplateDescriptor
        {
            Name = string.IsNullOrWhiteSpace(assemblyName) ? folderName : assemblyName!,
            Description = description ?? string.Empty,
            Version = version ?? "—",
            FullPath = templatePath,
        };
    }

    private static string? CaptureCsproj(string content, string tag)
    {
        var match = Regex.Match(content, @$"<{tag}>\s*(?<value>[^<]+?)\s*</{tag}>",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private async Task<ProjectModel?> BuildModelAsync(
        string repoPath,
        bool forceWorkspace,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var isGit = PathHygiene.IsRepositoryRoot(repoPath);
        var status = new GitResult(-1, string.Empty, string.Empty);
        var remotes = new GitResult(-1, string.Empty, string.Empty);
        var log = new GitResult(-1, string.Empty, string.Empty);

        if (isGit)
        {
            status = await GitAsync(repoPath, cancellationToken,
                "status", "--porcelain=v2", "--branch", "--untracked-files=normal").ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            remotes = await GitAsync(repoPath, cancellationToken,
                "remote", "--verbose").ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            log = await GitAsync(repoPath, cancellationToken,
                "log", $"-{CommitCount}", "--pretty=format:%h%x09%s%x09%an%x09%ar").ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var parsed = status.ExitCode == 0 ? ParseStatus(status.StdOut) : default;
        var (hasRemote, remoteOk, remoteHost) = ParseRemotes(remotes.StdOut);
        var (version, versionMismatch) = ReadVersion(repoPath, name);

        var branch = !isGit
            ? "—"
            : parsed.Detached || string.IsNullOrWhiteSpace(parsed.Branch)
                ? "detached"
                : parsed.Branch;

        var isWorkspace = forceWorkspace || CountProjectMarkers(repoPath, 0) >= 2;

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
            Dirty = parsed.Dirty,
            BehindUpstream = parsed.Behind > 0,
            StatusText = !isGit
                ? "Not a git repository"
                : StatusTextFor(hasRemote, parsed.Dirty, parsed.Behind),
            RecentCommits = ParseCommits(log.StdOut),
            Components = forceWorkspace ? BuildComponents(repoPath, name) : Array.Empty<ComponentInfo>(),
        };
    }

    private static Task<GitResult> GitAsync(string repoPath, CancellationToken cancellationToken, params string[] args)
    {
        var full = new string[GitPrelude.Length + args.Length];
        GitPrelude.CopyTo(full, 0);
        args.CopyTo(full, GitPrelude.Length);
        return GitRunner.RunAsync(repoPath, cancellationToken, full);
    }

    /// <summary>
    /// Parses porcelain v2 headers and entries. Public because it is the part
    /// most likely to break on a line-ending change, and a parser test does not
    /// need a repository to catch that.
    /// <para>
    /// v2 is used instead of v1 because its branch headers are stable and
    /// machine-readable (<c># branch.ab +2 -3</c>), which removes both the
    /// second <c>rev-list</c> call and any dependence on localised text.
    /// </para>
    /// </summary>
    public static StatusInfo ParseStatus(string stdout)
    {
        var branch = string.Empty;
        var detached = false;
        var unborn = false;
        var ahead = 0;
        var behind = 0;
        var dirty = false;

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    var value = line["# branch.head ".Length..].Trim();
                    if (string.Equals(value, "(detached)", StringComparison.Ordinal))
                    {
                        detached = true;
                    }
                    else
                    {
                        branch = value;
                    }
                }
                else if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
                {
                    unborn = line["# branch.oid ".Length..].Trim() == "(initial)";
                }
                else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    foreach (var token in line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (token.StartsWith('+') && int.TryParse(token[1..], out var a))
                        {
                            ahead = a;
                        }
                        else if (token.StartsWith('-') && int.TryParse(token[1..], out var b))
                        {
                            behind = b;
                        }
                    }
                }

                continue;
            }

            // Entries start with the record type: 1 (changed), 2 (renamed),
            // u (unmerged), ? (untracked). '!' is ignored and never counts.
            if (line[0] is '1' or '2' or 'u' or '?')
            {
                dirty = true;
            }
        }

        return new StatusInfo(branch, detached, unborn, ahead, behind, dirty);
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
            var parts = line.TrimEnd('\r').Split('\t');
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

    private static (string Version, bool Mismatch) ReadVersion(string repoPath, string folderName)
    {
        var projectFile = SelectProjectFile(PathHygiene.SafeEnumerateFiles(repoPath), folderName);
        if (projectFile is null)
        {
            return ("—", false);
        }

        string content;
        try
        {
            content = File.ReadAllText(projectFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    /// <summary>
    /// <c>&lt;FolderName&gt;.csproj</c> when it exists, otherwise the
    /// alphabetically first one. Never the first entry of an unordered listing:
    /// a card's version must not change between two scans of the same disk.
    /// </summary>
    public static string? SelectProjectFile(IEnumerable<string> files, string folderName)
    {
        var projectFiles = files
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projectFiles.Count == 0)
        {
            return null;
        }

        return projectFiles.FirstOrDefault(f =>
                   string.Equals(Path.GetFileNameWithoutExtension(f), folderName, StringComparison.OrdinalIgnoreCase))
               ?? projectFiles[0];
    }

    private static int CountProjectMarkers(string directory, int depth)
    {
        if (depth > MaxMarkerSearchDepth)
        {
            return 0;
        }

        var count = PathHygiene.SafeEnumerateFiles(directory).Count(IsMarker);
        foreach (var child in PathHygiene.SafeEnumerateDirectories(directory))
        {
            if (PathHygiene.IsIgnoredFolderName(Path.GetFileName(child)) || PathHygiene.IsReparsePoint(child))
            {
                continue;
            }

            count += CountProjectMarkers(child, depth + 1);
            if (count >= MaxMarkerFiles)
            {
                break;
            }
        }

        return count;
    }

    private static IReadOnlyList<ComponentInfo> BuildComponents(string repoPath, string repoName)
    {
        var components = new List<ComponentInfo>();

        var projectFile = SelectProjectFile(PathHygiene.SafeEnumerateFiles(repoPath), repoName);
        if (projectFile is not null)
        {
            components.Add(new ComponentInfo
            {
                Name = $"{repoName} app",
                RelativePath = $@"{repoName}\{Path.GetFileName(projectFile)}",
                Kind = "app",
            });
        }

        foreach (var dir in PathHygiene.SafeEnumerateDirectories(repoPath))
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

    private static string StatusTextFor(bool hasRemote, bool dirty, int behind)
    {
        if (!hasRemote)
        {
            return "No remote configured";
        }

        if (dirty)
        {
            return "Uncommitted changes";
        }

        return behind > 0 ? "Behind upstream" : "Clean working tree";
    }
}
