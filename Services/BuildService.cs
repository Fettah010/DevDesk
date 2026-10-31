using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// One build the user started: the operation in the activity centre plus the
/// problems collected while it ran. The problems object is replaced at the start
/// of every execution, so a Retry shows the new run's problems and not the old
/// run's.
/// </summary>
public sealed class BuildSession
{
    internal BuildSession() => Problems = new BuildProblemCollector();

    public OperationHandle Handle { get; internal set; } = null!;

    public BuildProblemCollector Problems { get; internal set; }

    /// <summary>
    /// The folder MSBuild reported for a successful publish (D-0.3.3). Null for
    /// every other verb and for a publish that did not report one.
    /// </summary>
    public string? OutputPath { get; internal set; }

    public string OperationId => Handle.Id;

    public Task<Operation> Completion => Handle.Completion;
}

/// <summary>
/// Build, run, publish and test through one process boundary (W-0.3.3).
/// <para>
/// It decides nothing about what the user wants (the page does), never touches
/// XAML, and never runs two builds at once: that is <see cref="ActivityCenter"/>'s
/// gate. What it owns is the translation from a <see cref="BuildProfile"/> to a
/// real process, the log file, the problems, and the honest outcome of each.
/// </para>
/// </summary>
public sealed class BuildService
{
    public const string BuildsFolderName = "builds";

    private readonly IProcessRunner _runner;
    private readonly ActivityCenter _activity;
    private readonly TimeProvider _time;
    private readonly PerfCounters _counters;
    private readonly string _logDirectory;

    public BuildService(
        IProcessRunner? runner = null,
        ActivityCenter? activity = null,
        TimeProvider? time = null,
        PerfCounters? counters = null,
        string? logDirectory = null)
    {
        _time = time ?? TimeProvider.System;
        _runner = runner ?? new ProcessRunner(_time);
        _activity = activity ?? AppServices.Activity;
        _counters = counters ?? PerfCounters.Current;
        _logDirectory = logDirectory ?? DefaultLogDirectory();
    }

    public ActivityCenter Activity => _activity;

    public string LogDirectory => _logDirectory;

    /// <summary>Where build logs live; honours <c>DEVDESK_DATA_DIR</c> like settings and app logs.</summary>
    public static string DefaultLogDirectory() =>
        Path.Combine(SettingsService.ResolveDataDirectory(), "Logs", BuildsFolderName);

    /// <summary>
    /// The profile for one project: settings defaults, then that project's saved
    /// override, then what the folder actually contains.
    /// </summary>
    public static BuildProfileResolution ResolveProfile(ProjectModel project, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);

        var key = NormalizePath(project.Path);
        var overrides = settings.BuildProfiles.TryGetValue(key, out var saved) ? saved : null;

        return BuildProfileResolver.Resolve(project.Path, settings.Build, overrides);
    }

    /// <summary>Whether Run can launch something, and the reason when it cannot.</summary>
    public static RunResolution ResolveRunTarget(BuildProfile profile) =>
        BuildOutputLocator.ResolveRunTarget(profile);

    public BuildSession Build(ProjectModel project, BuildProfile profile, IProgress<ProcessOutputLine>? console = null) =>
        Submit(OperationKind.Build, project, profile, console);

    public BuildSession Publish(ProjectModel project, BuildProfile profile, IProgress<ProcessOutputLine>? console = null) =>
        Submit(OperationKind.Publish, project, profile, console);

    public BuildSession Run(ProjectModel project, BuildProfile profile, IProgress<ProcessOutputLine>? console = null) =>
        Submit(OperationKind.Run, project, profile, console);

    /// <summary>
    /// The one fixed test profile (D-0.3.6): the app's own suite, the same two
    /// flags CI uses. Not a test runner — no tree, no per-test filter.
    /// </summary>
    public BuildSession TestSelf(ProjectModel self, AppSettings settings, IProgress<ProcessOutputLine>? console = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(settings);

        var tests = Path.Combine(self.Path, "DevDesk.Tests", "DevDesk.Tests.csproj");
        var profile = new BuildProfile(
            self.Path,
            tests,
            "DevDesk.Tests.csproj",
            BuildProfileResolver.DefaultConfiguration,
            settings.Build.Platform,
            PlatformApplies: true,
            NoRestore: false,
            ExtraArguments: Array.Empty<string>());

        return Submit(OperationKind.Test, self, profile, console);
    }

    private BuildSession Submit(
        OperationKind kind,
        ProjectModel project,
        BuildProfile profile,
        IProgress<ProcessOutputLine>? console)
    {
        var session = new BuildSession();

        var request = new OperationRequest(
            kind,
            project.Name,
            project.Path,
            $"{Verb(kind)} {project.Name}",
            BuildCommandBuilder.Preview(ArgumentsFor(kind, profile)),
            (token, output) => ExecuteAsync(kind, project, profile, session, token, output),
            console,
            profile.Configuration,
            profile.Platform);

        session.Handle = _activity.Submit(request);
        return session;
    }

    private async Task<OperationOutcome> ExecuteAsync(
        OperationKind kind,
        ProjectModel project,
        BuildProfile profile,
        BuildSession session,
        CancellationToken cancellationToken,
        IProgress<ProcessOutputLine>? console)
    {
        session.Problems = new BuildProblemCollector();

        var started = _time.GetUtcNow();
        var logPath = BuildLogFile.Create(_logDirectory, kind, profile.TargetName, started);

        using var log = new BuildLogWriter(logPath);
        var pump = new BuildOutputPump(console, log, session.Problems, _time);

        pump.WriteLine($"{Verb(kind)} {project.Name}");
        pump.WriteLine($"command: {BuildCommandBuilder.Preview(ArgumentsFor(kind, profile))}");
        pump.WriteLine($"folder:  {profile.ProjectPath}");
        pump.WriteLine($"log:     {logPath}");
        pump.WriteLine(string.Empty);

        var began = _time.GetTimestamp();
        ProcessResult result;

        try
        {
            result = await ExecuteCoreAsync(kind, profile, pump, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A runner that throws on cancellation is still a user stop, not a
            // failure of the tool.
            _counters.RecordBuild(_time.GetElapsedTime(began, _time.GetTimestamp()), PerfOutcome.Skipped);
            throw;
        }
        catch
        {
            _counters.RecordBuild(_time.GetElapsedTime(began, _time.GetTimestamp()), PerfOutcome.Failed);
            throw;
        }

        _counters.RecordBuild(
            _time.GetElapsedTime(began, _time.GetTimestamp()),
            result.Outcome switch
            {
                ProcessOutcome.Succeeded => PerfOutcome.Ok,
                ProcessOutcome.Canceled => PerfOutcome.Skipped,
                _ => PerfOutcome.Failed,
            });

        // Publish is the one verb whose output is a folder the user wants to
        // open. Take it from the line MSBuild wrote (D-0.3.3); a failed publish
        // has no folder worth opening.
        if (kind == OperationKind.Publish
            && result.Outcome == ProcessOutcome.Succeeded
            && pump.OutputPath is { } output)
        {
            session.OutputPath = NormalizePath(output);
        }

        log.Flush();
        return Outcome(result, session.Problems, logPath, session.OutputPath);
    }

    private async Task<ProcessResult> ExecuteCoreAsync(
        OperationKind kind,
        BuildProfile profile,
        BuildOutputPump pump,
        CancellationToken cancellationToken)
    {
        if (kind != OperationKind.Run)
        {
            return await _runner
                .RunAsync(RequestFor(kind, profile), pump, cancellationToken)
                .ConfigureAwait(false);
        }

        // Run = build, then start the exe the build produced (D-0.3.2).
        var build = await _runner
            .RunAsync(BuildCommandBuilder.BuildRequest(profile), pump, cancellationToken)
            .ConfigureAwait(false);

        if (!build.Succeeded)
        {
            pump.WriteLine("Nothing was started: the build did not succeed.");
            return build;
        }

        var run = BuildOutputLocator.ResolveRunTarget(profile);
        if (!run.CanRun)
        {
            pump.WriteLine(run.Reason ?? "There is nothing to run here.");
            return new ProcessResult(ProcessOutcome.NotFound, build.ExitCode, string.Empty, build.Duration, run.Reason);
        }

        var executable = BuildOutputLocator.FindExecutable(profile, run);
        if (executable is null)
        {
            pump.WriteLine("No built executable was found; falling back to dotnet run.");
            return await _runner
                .RunAsync(BuildCommandBuilder.RunRequest(profile), pump, cancellationToken)
                .ConfigureAwait(false);
        }

        pump.WriteLine($"Starting {executable}");
        return await _runner
            .RunAsync(
                new ProcessRequest(
                    executable,
                    Array.Empty<string>(),
                    profile.ProjectPath,
                    Environment: null,
                    Timeout: TimeSpan.Zero),
                pump,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ProcessRequest RequestFor(OperationKind kind, BuildProfile profile) => kind switch
    {
        OperationKind.Publish => BuildCommandBuilder.PublishRequest(profile),
        OperationKind.Test => BuildCommandBuilder.TestRequest(profile),
        _ => BuildCommandBuilder.BuildRequest(profile),
    };

    private static System.Collections.Generic.IReadOnlyList<string> ArgumentsFor(
        OperationKind kind,
        BuildProfile profile) => kind switch
    {
        OperationKind.Publish => BuildCommandBuilder.PublishArguments(profile),
        OperationKind.Test => BuildCommandBuilder.TestArguments(profile),
        _ => BuildCommandBuilder.BuildArguments(profile),
    };

    private static OperationOutcome Outcome(
        ProcessResult result,
        BuildProblemCollector problems,
        string logPath,
        string? outputPath) => result.Outcome switch
    {
        ProcessOutcome.Succeeded => OperationOutcome.Succeeded(result.ExitCode, logPath, outputPath),
        ProcessOutcome.Canceled => OperationOutcome.Canceled(logPath),
        ProcessOutcome.TimedOut => OperationOutcome.Failed(
            result.ExitCode,
            problems.FirstError,
            logPath,
            result.FailureHint),
        ProcessOutcome.NotFound => OperationOutcome.Failed(
            null,
            problems.FirstError,
            logPath,
            result.FailureHint),
        _ => OperationOutcome.Failed(
            result.ExitCode,
            problems.FirstError ?? result.FailureHint,
            logPath,
            result.FailureHint),
    };

    private static string Verb(OperationKind kind) => kind switch
    {
        OperationKind.Build => "Build",
        OperationKind.Run => "Run",
        OperationKind.Publish => "Publish",
        OperationKind.Test => "Test",
        _ => "Build",
    };

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}
