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

    public IReadOnlyList<ScanRootDiagnostic> RootDiagnostics { get; init; } = Array.Empty<ScanRootDiagnostic>();
}

public sealed record ScanRootDiagnostic(string RootPath, string Location, string Message);

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

public interface IScanRootEngine : IScanEngine
{
    Task<ScannerResult> ScanRootAsync(
        ScanRootSettings root,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null);
}

public interface IScanCheckoutEngine : IScanEngine
{
    Task<ProjectModel?> ScanCheckoutAsync(
        ScanRootSettings root,
        string checkoutPath,
        CancellationToken cancellationToken = default);
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
/// Walks the configured enabled roots, finds git
/// repos / project-marked folders, and reads read-only git state + csproj
/// versions per the scan model in AGENTS.md:
///
///  * The default root is DevEcosystem at depth 2; additional roots have their
///    own configured depth and ordered ignore rules.
///  * DevDesk itself is never a scanned project — it becomes the self
///    workspace (<see cref="ScannerResult.Self"/>), located by
///    <see cref="SelfLocator"/>, never by folder name.
///  * Stop descending once a repo is found (its contents are not more projects).
///  * User ignores are root-relative; <see cref="PathHygiene"/> keeps Git
///    metadata and reparse points outside user-negatable rules.
///  * Local-only: no network calls, no writes (no locks either), a broken git
///    never crashes a scan, and a cancelled scan stops promptly.
/// </summary>
public sealed class ScanEngine : IScanRootEngine, IScanCheckoutEngine
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
    private readonly Func<IReadOnlyList<ScanRootSettings>>? _rootsProvider;

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
        int? maxParallelism = null,
        Func<IReadOnlyList<ScanRootSettings>>? rootsProvider = null)
    {
        _root = root ?? DefaultScanRoot;
        _selfPath = NormalizeSelf(selfPath ?? SelfLocator.FindSourceRepo(AppContext.BaseDirectory));
        _maxDepth = Math.Max(1, maxDepth);
        _maxParallelism = Math.Max(1, maxParallelism ?? Math.Min(4, Environment.ProcessorCount));
        _rootsProvider = rootsProvider;
    }

    public string ScanRoot =>
        _rootsProvider?.Invoke().FirstOrDefault(root => root.Enabled)?.Path ?? _root;

    public string? SelfPath => _selfPath;

    public async Task<ScannerResult> ScanAsync(
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        var roots = GetRoots();
        var candidates = new List<ScanCandidate>();
        var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templateCandidates = new List<string>();
        var templatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootDiagnostics = new List<ScanRootDiagnostic>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!root.Enabled)
            {
                continue;
            }

            string rootPath;
            try
            {
                rootPath = PathHygiene.Canonicalize(root.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                rootDiagnostics.Add(new ScanRootDiagnostic(root.Path, string.Empty, $"Invalid root path: {ex.Message}"));
                continue;
            }

            if (!Directory.Exists(rootPath))
            {
                rootDiagnostics.Add(new ScanRootDiagnostic(rootPath, string.Empty, "Root is missing or inaccessible."));
                continue;
            }

            if (!PathHygiene.TryEnumerateDirectories(rootPath, out _, out var rootError))
            {
                rootDiagnostics.Add(new ScanRootDiagnostic(rootPath, string.Empty, $"Cannot read root: {rootError}"));
                continue;
            }

            if (!IsSelf(rootPath)
                && !PathHygiene.IsReparsePoint(rootPath)
                && (PathHygiene.IsRepositoryRoot(rootPath) || HasProjectMarker(rootPath)))
            {
                if (candidatePaths.Add(rootPath))
                {
                    candidates.Add(new ScanCandidate(rootPath, rootPath));
                    progress?.Report(new ScanProgress(
                        0,
                        0,
                        Path.GetFileName(rootPath),
                        rootPath,
                        CreatePendingModel(rootPath, rootPath)));
                }

                continue;
            }

            Collect(
                rootPath,
                root.Depth,
                new IgnoreRuleMatcher(root.Ignores),
                rootPath,
                1,
                candidates,
                candidatePaths,
                templateCandidates,
                templatePaths,
                cancellationToken,
                progress,
                rootDiagnostics);
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
                var candidate = candidates[index];
                var model = await BuildModelAsync(
                        candidate.Path,
                        forceWorkspace: false,
                        token,
                        candidate.RootPath)
                    .ConfigureAwait(false);

                models[index] = model;
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ScanProgress(
                    done,
                    candidates.Count,
                    model?.Name ?? Path.GetFileName(candidate.Path),
                    candidate.RootPath,
                    model));
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var self = await selfTask.ConfigureAwait(false);

        // The array is indexed by candidate order, so the result is stable even
        // though the repos are read concurrently.
        var projects = models.Where(m => m is not null).Select(m => m!).ToList();
        return new ScannerResult
        {
            Self = self,
            Projects = projects,
            Templates = templates,
            RootDiagnostics = rootDiagnostics,
        };
    }

    public Task<ScannerResult> ScanRootAsync(
        ScanRootSettings root,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var singleRootEngine = new ScanEngine(
            root.Path,
            _selfPath,
            root.Depth,
            _maxParallelism,
            () => new[] { root });
        return singleRootEngine.ScanAsync(cancellationToken, progress);
    }

    public async Task<ProjectModel?> ScanCheckoutAsync(
        ScanRootSettings root,
        string checkoutPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutPath);
        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = PathHygiene.Canonicalize(root.Path);
        var candidatePath = PathHygiene.Canonicalize(checkoutPath);
        if (!PathHygiene.IsUnder(candidatePath, rootPath)
            || !Directory.Exists(candidatePath)
            || PathHygiene.IsReparsePoint(candidatePath)
            || IsSelf(candidatePath))
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (new IgnoreRuleMatcher(root.Ignores).IsExcluded(relativePath)
            || (!PathHygiene.IsRepositoryRoot(candidatePath) && !HasProjectMarker(candidatePath)))
        {
            return null;
        }

        return await BuildModelAsync(
                candidatePath,
                forceWorkspace: false,
                cancellationToken,
                rootPath)
            .ConfigureAwait(false);
    }

    private sealed record ScanCandidate(string Path, string RootPath);

    /// <summary>
    /// Collects candidate folders without reading any git state. Discovery is
    /// ordered, so the same disk always produces the same card order.
    /// Also collects template candidates that match the template marker pattern.
    /// </summary>
    private void Collect(
        string root,
        int maxDepth,
        IgnoreRuleMatcher ignores,
        string directory,
        int depth,
        List<ScanCandidate> candidates,
        HashSet<string> candidatePaths,
        List<string> templateCandidates,
        HashSet<string> templatePaths,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress,
        List<ScanRootDiagnostic> rootDiagnostics)
    {
        if (!PathHygiene.TryEnumerateDirectories(directory, out var children, out var error))
        {
            rootDiagnostics.Add(new ScanRootDiagnostic(
                root,
                Path.GetRelativePath(root, directory),
                $"Cannot read folder: {error}"));
            return;
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(child);
            var relativePath = Path.GetRelativePath(root, child);
            var excluded = ignores.IsExcluded(relativePath);
            if (PathHygiene.IsSafetyIgnoredFolderName(name)
                || PathHygiene.IsReparsePoint(child)
                || IsSelf(child)
                || (excluded && !ignores.MayContainNegatedPath(relativePath)))
            {
                continue;
            }

            if (!excluded && (PathHygiene.IsRepositoryRoot(child) || HasProjectMarker(child)))
            {
                if (candidatePaths.Add(PathHygiene.Canonicalize(child)))
                {
                    candidates.Add(new ScanCandidate(child, root));
                    progress?.Report(new ScanProgress(
                        0,
                        0,
                        name,
                        root,
                        CreatePendingModel(child, root)));
                }

                continue;
            }

            if (!excluded && HasTemplateMarker(child))
            {
                if (templatePaths.Add(PathHygiene.Canonicalize(child)))
                {
                    templateCandidates.Add(child);
                    progress?.Report(new ScanProgress(0, 0, name + " (template)"));
                }

                continue;
            }

            if (depth < maxDepth)
            {
                Collect(
                    root,
                    maxDepth,
                    ignores,
                    child,
                    depth + 1,
                    candidates,
                    candidatePaths,
                    templateCandidates,
                    templatePaths,
                    cancellationToken,
                    progress,
                    rootDiagnostics);
            }
        }
    }

    private IReadOnlyList<ScanRootSettings> GetRoots()
    {
        if (_rootsProvider is not null)
        {
            return _rootsProvider();
        }

        return new[]
        {
            new ScanRootSettings
            {
                Path = _root,
                Depth = _maxDepth,
            },
        };
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

    private static ProjectModel CreatePendingModel(string path, string root) =>
        new()
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Path = path,
            ScanRootPath = root,
            Branch = "Checking…",
            Version = "—",
            HasRemote = false,
            RemoteOk = false,
            RemoteHost = "Checking",
            StatusText = "Reading local project details…",
            DetailsPending = true,
        };

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
        CancellationToken cancellationToken,
        string? scanRootPath = null)
    {
        var name = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var isGit = PathHygiene.IsRepositoryRoot(repoPath);
        var identity = isGit ? RepositoryIdentityResolver.Resolve(repoPath) : null;
        var status = new GitResult(-1, string.Empty, string.Empty);
        var remotes = new GitResult(-1, string.Empty, string.Empty);
        var log = new GitResult(-1, string.Empty, string.Empty);

        if (isGit)
        {
            var statusTask = GitAsync(repoPath, cancellationToken,
                "status", "--porcelain=v2", "--branch", "--untracked-files=normal");
            var remotesTask = identity is not null && TryReadRemoteResult(identity.CommonDirectory, out var remoteResult)
                ? Task.FromResult(remoteResult)
                : GitAsync(repoPath, cancellationToken, "remote", "--verbose");
            Task<GitResult> logTask;
            if (identity is not null && IsUnbornRepository(identity, out var unborn) && unborn)
            {
                logTask = Task.FromResult(new GitResult(0, string.Empty, string.Empty));
            }
            else
            {
                logTask = GitAsync(repoPath, cancellationToken,
                    "log", $"-{CommitCount}", "--pretty=format:%h%x09%s%x09%an%x09%ar%x09%cI");
            }
            await Task.WhenAll(statusTask, remotesTask, logTask).ConfigureAwait(false);
            status = await statusTask.ConfigureAwait(false);
            remotes = await remotesTask.ConfigureAwait(false);
            log = await logTask.ConfigureAwait(false);
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
            ScanRootPath = scanRootPath ?? string.Empty,
            RepositoryIdentity = identity?.CommonDirectory ?? string.Empty,
            IsWorktree = identity?.IsWorktree ?? false,
            IsSubmodule = identity?.IsSubmodule ?? false,
            WorktreeOfPath = identity?.WorktreeOfPath,
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

    private static bool TryReadRemoteResult(string commonDirectory, out GitResult result)
    {
        result = new GitResult(-1, string.Empty, string.Empty);
        var configPath = Path.Combine(commonDirectory, "config");
        try
        {
            var content = File.ReadAllText(configPath);
            if (Regex.IsMatch(content, @"^\s*\[\s*include(?:If)?\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                || Regex.IsMatch(content, @"^\s*worktreeConfig\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                return false;
            }

            var urls = new List<(string Name, string Url)>();
            string? remoteName = null;
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim().TrimEnd('\r');
                var section = Regex.Match(line, "^\\[remote\\s+\"(?<name>[^\"]+)\"\\]$", RegexOptions.IgnoreCase);
                if (section.Success)
                {
                    remoteName = section.Groups["name"].Value;
                    continue;
                }

                if (line.StartsWith('['))
                {
                    remoteName = null;
                    continue;
                }

                if (remoteName is null)
                {
                    continue;
                }

                var setting = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (setting.Length == 2 && string.Equals(setting[0], "url", StringComparison.OrdinalIgnoreCase))
                {
                    var url = setting[1].Trim().Trim('"');
                    if (url.Length > 0)
                    {
                        urls.Add((remoteName, url));
                    }
                }
            }

            var output = urls.SelectMany(remote => new[]
            {
                $"{remote.Name}\t{remote.Url} (fetch)",
                $"{remote.Name}\t{remote.Url} (push)",
            });
            result = new GitResult(0, string.Join('\n', output), string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUnbornRepository(RepositoryIdentityInfo identity, out bool unborn)
    {
        unborn = false;
        try
        {
            var head = File.ReadAllText(Path.Combine(identity.GitDirectory, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                return true;
            }

            var reference = head["ref: ".Length..].Trim();
            if (reference.Length == 0 || Path.IsPathRooted(reference)
                || reference.Split('/', '\\').Any(segment => segment is "." or ".."))
            {
                return false;
            }

            var referenceExists = File.Exists(Path.Combine(identity.GitDirectory, reference))
                || File.Exists(Path.Combine(identity.CommonDirectory, reference));
            var packedReferences = Path.Combine(identity.CommonDirectory, "packed-refs");
            if (!referenceExists && File.Exists(packedReferences))
            {
                referenceExists = File.ReadLines(packedReferences)
                    .Any(line => !line.StartsWith('#') && !line.StartsWith('^')
                        && line.EndsWith(" " + reference, StringComparison.Ordinal));
            }

            unborn = !referenceExists;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
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
                CommittedAt = parts.Length >= 5
                    && DateTimeOffset.TryParse(parts[4], out var committedAt)
                        ? committedAt
                        : null,
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
