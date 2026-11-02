using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;
using GitInitResult = DevDesk.Models.GitResult;

namespace DevDesk.Services;

/// <summary>
/// Orchestrates the full template-to-app chain: copy, optional git init +
/// first commit, smoke build, and a <see cref="WizardResult"/> the UI can
/// render. Never touches the template source.
/// </summary>
public sealed class TemplateCreationService
{
    private readonly TemplateCopyService _copy;
    private readonly BuildService _builds;
    private readonly TimeProvider _time;

    public TemplateCreationService(
        TemplateCopyService? copy = null,
        BuildService? builds = null,
        TimeProvider? time = null)
    {
        _copy = copy ?? new TemplateCopyService();
        _builds = builds ?? new BuildService(activity: AppServices.Activity);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs the full create chain for <paramref name="request"/>:
    /// <list type="number">
    /// <item>Validate the target is outside the template (W-0.4.7).</item>
    /// <item>Copy the template with token substitution + renames (W-0.4.4).</item>
    /// <item>Optionally `git init` + first commit (D-0.4.6).</item>
    /// <item>Smoke `dotnet build` via <see cref="BuildService"/> (W-0.4.5).</item>
    /// </list>
    /// </summary>
    public async Task<WizardResult> CreateAsync(
        CreateRequest request,
        IProgress<TemplateCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var template = request.Template;
        var inputs = request.Inputs;
        var options = inputs.Options;

        // W-0.4.7: refuse if the target would touch the template.
        TemplateSafetyGuard.ValidateTargetOutsideTemplate(inputs.Location, template.FullPath);

        // Build the token map: this computes SafeName from the raw Name.
        var tokens = TokenMap.Create(
            inputs.Name,
            packAuthors: options.PackAuthors,
            repoUrl: options.RepoUrl,
            version: options.Version);
        var safeName = tokens.SafeName;

        var targetPath = Path.Combine(inputs.Location, safeName);

        // Don't overwrite an existing folder (D-0.4.2).
        if (Directory.Exists(targetPath))
        {
            throw new InvalidOperationException(
                $"Target folder '{targetPath}' already exists. Choose a different name or location.");
        }

        // W-0.4.4: copy with tokens and renames; rollback on cancel/failure.
        var renamePlans = new List<RenamePlan>();
        foreach (var file in EnumerateTemplateFiles(template.FullPath))
        {
            var plan = RenamePlanner.Plan(file, template.FullPath, targetPath, tokens);
            if (plan is not null)
            {
                renamePlans.Add(plan);
            }
        }

        var copyProgress = new WrapperProgress(progress);
        var copy = await _copy.CopyAsync(
            template.FullPath,
            targetPath,
            tokens,
            renamePlans,
            copyProgress,
            cancellationToken).ConfigureAwait(false);

        if (copy.Cancelled)
        {
            return new WizardResult(
                TargetPath: targetPath,
                BuildInfo: new BuildResult(
                    targetPath, inputs.Name, OperationStatus.Canceled,
                    copy.Duration, null, null, null),
                GitInfo: null);
        }

        // W-0.4.5: optional git init + first commit.
        GitInitResult? gitInfo = null;
        if (options.InitGit)
        {
            gitInfo = await GitInitAndCommitAsync(
                targetPath, options.AuthorName, cancellationToken).ConfigureAwait(false);
        }

        // W-0.4.5: smoke build via BuildService (same profile resolution as an
        // existing project).
        var buildResult = await SmokeBuildAsync(
            targetPath, inputs.Name, safeName, cancellationToken).ConfigureAwait(false);

        return new WizardResult(
            TargetPath: targetPath,
            BuildInfo: buildResult,
            GitInfo: gitInfo);
    }

    /// <summary>Runs `git init` + first commit in the target folder.</summary>
    private async Task<GitInitResult> GitInitAndCommitAsync(
        string targetPath,
        string? authorName,
        CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();

        try
        {
            // git init
            var init = await GitRunner.RunAsync(targetPath, cancellationToken, "init").ConfigureAwait(false);
            if (init.ExitCode != 0)
            {
                return new GitInitResult(
                    false, targetPath, null, init.StdErr?.Trim() ?? "git init failed");
            }

            // git add -A
            var add = await GitRunner.RunAsync(targetPath, cancellationToken, "add", "-A").ConfigureAwait(false);
            if (add.ExitCode != 0)
            {
                return new GitInitResult(
                    false, targetPath, null, add.StdErr?.Trim() ?? "git add failed");
            }

            // git commit
            var commitArgs = new List<string> { "commit", "-m", "Initial commit from DevDesk template", "--quiet" };
            if (!string.IsNullOrWhiteSpace(authorName))
            {
                commitArgs.Add("--author");
                commitArgs.Add($"{authorName} <no-reply@devdesk.io>");
            }

            var commit = await GitRunner.RunAsync(targetPath, cancellationToken, commitArgs.ToArray()).ConfigureAwait(false);
            if (commit.ExitCode != 0)
            {
                return new GitInitResult(
                    false, targetPath, null, commit.StdErr?.Trim() ?? "git commit failed");
            }

            // Read the first commit hash.
            var log = await GitRunner.RunAsync(targetPath, cancellationToken,
                "log", "-1", "--pretty=format:%H").ConfigureAwait(false);
            var hash = log.ExitCode == 0 ? log.StdOut?.Trim() : null;

            return new GitInitResult(true, targetPath, hash, null);
        }
        catch (Exception ex)
        {
            return new GitInitResult(false, targetPath, null, ex.Message);
        }
    }

    /// <summary>Runs `dotnet build` on the new app's csproj via BuildService.</summary>
    private async Task<BuildResult> SmokeBuildAsync(
        string targetPath,
        string appName,
        string safeName,
        CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();

        // Resolve the project file: ${SafeName}.csproj at the target root.
        var projectFile = Path.Combine(targetPath, $"{safeName}.csproj");
        if (!File.Exists(projectFile))
        {
            // Fall back to the first csproj found.
            foreach (var file in PathHygiene.SafeEnumerateFiles(targetPath))
            {
                if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    projectFile = file;
                    break;
                }
            }
        }

        // Build a lightweight ProjectModel so BuildService can resolve a profile.
        var project = new ProjectModel
        {
            Name = appName,
            Path = targetPath,
            IconGlyph = "\uE8B7",
            IsWorkspace = false,
            Branch = "—",
            Version = "—",
            VersionMismatch = false,
            HasRemote = false,
            RemoteOk = false,
            RemoteHost = "local only",
            Dirty = false,
            BehindUpstream = false,
            StatusText = "No remote configured",
            RecentCommits = Array.Empty<CommitInfo>(),
            Components = Array.Empty<ComponentInfo>(),
        };

        // The app's settings decide Debug/Release and platform.
        var settings = AppServices.Settings.Settings;
        var resolution = BuildService.ResolveProfile(project, settings);
        if (resolution.Profile is null)
        {
            var buildElapsed = _time.GetElapsedTime(started, _time.GetTimestamp());
            return new BuildResult(
                targetPath, appName, OperationStatus.Failed, buildElapsed, null,
                null, null);
        }

        // Override the target to point at the new app's csproj.
        var buildProfile = resolution.Profile with
        {
            TargetPath = projectFile,
            TargetName = Path.GetFileName(projectFile),
        };

        var session = _builds.Build(project, buildProfile);
        var finished = await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

        var elapsed = _time.GetElapsedTime(started, _time.GetTimestamp());
        return new BuildResult(
            targetPath,
            appName,
            finished.Status,
            elapsed,
            finished.ExitCode,
            finished.LogPath,
            session.OutputPath);
    }

    /// <summary>Enumerates every file under the template root (skip-safe).</summary>
    private static IEnumerable<string> EnumerateTemplateFiles(string root)
    {
        foreach (var file in PathHygiene.SafeEnumerateFiles(root))
        {
            yield return file;
        }

        foreach (var dir in PathHygiene.SafeEnumerateDirectories(root))
        {
            foreach (var file in EnumerateTemplateFiles(dir))
            {
                yield return file;
            }
        }
    }

    /// <summary>Adapts <see cref="TemplateCopyProgress"/> to <see cref="TemplateCopyService.Progress"/>.</summary>
    private sealed class WrapperProgress : IProgress<TemplateCopyService.Progress>
    {
        private readonly IProgress<TemplateCopyProgress>? _outer;

        public WrapperProgress(IProgress<TemplateCopyProgress>? outer)
        {
            _outer = outer;
        }

        public void Report(TemplateCopyService.Progress value) =>
            _outer?.Report(new TemplateCopyProgress(value.FileName, value.FilesDone, value.FilesTotal));
    }
}
