using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

/// <summary>How a process call ended. A cancel is not a failure.</summary>
public enum ProcessOutcome
{
    Succeeded,
    Failed,
    Canceled,
    TimedOut,

    /// <summary>The executable or its working folder does not exist.</summary>
    NotFound,
}

/// <summary>
/// One process invocation. Arguments are a LIST, never a composed command string:
/// the ecosystem contains paths with spaces (<c>DevTem WinUI 3</c>), and
/// interpolation is how a path becomes two arguments (AGENTS gotcha 7).
/// </summary>
public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null)
{
    public static ProcessRequest For(string fileName, string workingDirectory, params string[] arguments) =>
        new(fileName, arguments, workingDirectory);
}

/// <summary>One line of output, tagged so the console can colour it and the file can keep it.</summary>
public sealed record ProcessOutputLine(string Text, bool IsError, DateTimeOffset At);

/// <summary>
/// The result of one process call. <see cref="StandardErrorTail"/> is capped on
/// purpose: the full stream is in the build log, and a result object is not a
/// place to keep a megabyte of text.
/// </summary>
public sealed record ProcessResult(
    ProcessOutcome Outcome,
    int ExitCode,
    string StandardErrorTail,
    TimeSpan Duration,
    string? FailureHint)
{
    public bool Succeeded => Outcome == ProcessOutcome.Succeeded;
}

/// <summary>
/// The only way the app starts a process for a build. Injectable so a test can
/// script output, cancellation and failure without spawning a real compiler.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken);
}

/// <summary>
/// A safe, streaming process runner (roadmap §3.6, W-0.3.1).
/// <para>
/// It exists because <see cref="GitRunner"/> answers a different question: git
/// output is small, captured to the end, and a timeout is always a failure. A
/// build is the opposite — output is unbounded and arrives for minutes, the user
/// must be able to stop it, and "cancelled" is a normal outcome rather than an
/// error. Both keep the same non-negotiables: argument lists, no shell, no
/// window, redirected UTF-8 pipes, and a tree kill so no compiler survives.
/// </para>
/// <para>
/// Cancellation and timeout are returned as outcomes instead of thrown, because
/// the caller has to record what happened in the activity history; a thrown
/// exception would make the build that the user stopped indistinguishable from
/// the one that crashed.
/// </para>
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Roadmap §3.6: a build or publish gets 30 minutes, not 30 seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private const int ErrorTailLength = 4000;

    private readonly TimeProvider _time;

    public ProcessRunner(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// Environment every build inherits. The UI language is pinned so a localized
    /// SDK cannot change the shape of a diagnostic the parser reads; telemetry and
    /// the first-run banner are off because nothing here is interactive.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultEnvironment { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = _time.GetTimestamp();

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return Result(ProcessOutcome.NotFound, -1, string.Empty, started, "No executable was specified.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
        {
            return Result(
                ProcessOutcome.NotFound,
                -1,
                string.Empty,
                started,
                $"The folder \"{request.WorkingDirectory}\" does not exist.");
        }

        var startInfo = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var pair in DefaultEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            return Result(
                ProcessOutcome.NotFound,
                -1,
                string.Empty,
                started,
                $"{request.FileName} was not found. Check that it is installed and on PATH.");
        }

        if (process is null)
        {
            return Result(
                ProcessOutcome.NotFound,
                -1,
                string.Empty,
                started,
                $"{request.FileName} could not be started.");
        }

        // The arguments are joined for the LOG only; execution always used the
        // list above. Redaction keeps a token in an argument out of the log.
        LoggingService.Log.Information(
            "Process {File} {Arguments} in {Directory}",
            request.FileName,
            LogRedaction.Redact(string.Join(' ', request.Arguments)),
            request.WorkingDirectory);

        var errorTail = new StringBuilder();
        var tailGate = new object();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            output?.Report(new ProcessOutputLine(AnsiText.Clean(args.Data), false, _time.GetUtcNow()));
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (tailGate)
            {
                if (errorTail.Length < ErrorTailLength)
                {
                    errorTail.AppendLine(args.Data);
                }
            }

            output?.Report(new ProcessOutputLine(AnsiText.Clean(args.Data), true, _time.GetUtcNow()));
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = request.Timeout ?? DefaultTimeout;
        var timedOut = false;

        using (process)
        {
            try
            {
                if (timeout > TimeSpan.Zero)
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    limit.CancelAfter(timeout);
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                else
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                Kill(process);
                await DrainAfterKillAsync(process).ConfigureAwait(false);

                var tail = Tail(errorTail, tailGate);
                var outcome = timedOut ? ProcessOutcome.TimedOut : ProcessOutcome.Canceled;
                var hint = timedOut
                    ? $"Stopped after {FormatTimeout(timeout)}."
                    : "Cancelled.";

                LoggingService.Log.Information(
                    "Process {File} {Outcome} after {Elapsed}",
                    request.FileName,
                    outcome,
                    PerfCounters.FormatDuration(_time.GetElapsedTime(started, _time.GetTimestamp())));

                return Result(outcome, -1, tail, started, hint);
            }

            // The async readers raise their last events after the process exits;
            // this call is the documented way to wait for them, and it returns
            // immediately because the process has already exited.
            process.WaitForExit();

            var exitCode = process.ExitCode;
            var finalOutcome = exitCode == 0 ? ProcessOutcome.Succeeded : ProcessOutcome.Failed;
            var finalHint = exitCode == 0 ? null : $"Exited with code {exitCode}.";

            LoggingService.Log.Information(
                "Process {File} exited {Code} in {Elapsed}",
                request.FileName,
                exitCode,
                PerfCounters.FormatDuration(_time.GetElapsedTime(started, _time.GetTimestamp())));

            return Result(finalOutcome, exitCode, Tail(errorTail, tailGate), started, finalHint);
        }
    }

    private ProcessResult Result(
        ProcessOutcome outcome,
        int exitCode,
        string standardError,
        long started,
        string? hint) =>
        new(
            outcome,
            exitCode,
            standardError,
            _time.GetElapsedTime(started, _time.GetTimestamp()),
            hint);

    private static string Tail(StringBuilder buffer, object gate)
    {
        lock (gate)
        {
            return buffer.ToString().TrimEnd();
        }
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.FromMinutes(1)
            ? $"{timeout.TotalMinutes:0} minutes"
            : $"{timeout.TotalSeconds:0} seconds";

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or the tree was reaped by the OS: nothing to do.
        }
    }

    /// <summary>
    /// Waits (bounded) for a killed process to be reaped so the exit code and the
    /// reader events are not read from a process that is still tearing down.
    /// </summary>
    private static async Task DrainAfterKillAsync(Process process)
    {
        try
        {
            var exit = process.WaitForExitAsync();
            await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        catch
        {
            // A killed process can close its pipes mid-read; the call already ended.
        }
    }
}
