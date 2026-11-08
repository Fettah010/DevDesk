using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

/// <summary>Outcome of one git process call.</summary>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Safe git process runner: argument LISTS (never interpolated shell strings —
/// see AGENTS gotcha #7), redirected output, a hard timeout, and cancellation.
/// It does not decide whether a command is read-only or networked; the scanner
/// calls only read-only commands, while <see cref="GitService"/> owns explicit
/// user-confirmed operations.
/// <para>
/// Every process runs non-interactively (see <see cref="NonInteractiveEnvironment"/>):
/// a scan that stops on a credential prompt would hang the whole app, and the
/// user is never at a terminal to answer it.
/// </para>
/// <para>
/// The executable is resolved explicitly instead of relying on PATH: a host
/// whose PATH selects a slow bundled Git makes every repository read costly
/// (M4), so installed Git is preferred and the resolved exe is reported in
/// Diagnostics.
/// </para>
/// </summary>
public static class GitRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const string DevDeskGitExeEnv = "DEVDESK_GIT_EXE";

    /// <summary>
    /// Common installed-Git locations, 64-bit first. A machine whose PATH
    /// selects a Copilot-bundled Git can scan 50 repositories in ~8 s; the
    /// installed Git at one of these locations does it in ~2 s.
    /// </summary>
    private static readonly string[] InstalledGitCandidates = new[]
    {
        @"D:\Program Files\Git\cmd\git.exe",
        @"C:\Program Files\Git\cmd\git.exe",
        @"C:\Program Files (x86)\Git\cmd\git.exe",
        @"D:\Program Files (x86)\Git\cmd\git.exe",
    };

    /// <summary>
    /// Programmatic override of the resolved executable, mainly for tests and
    /// deployments. Setting it clears the cached resolution. The
    /// <c>DEVDESK_GIT_EXE</c> environment variable is read first.
    /// </summary>
    public static string? GitExeOverride
    {
        get => _gitExeOverride;
        set { _gitExeOverride = value; _gitExePath = null; }
    }
    private static string? _gitExeOverride;

    /// <summary>Resolved git executable path, or "git" to fall back to PATH.</summary>
    public static string GitExePath
    {
        get
        {
            if (_gitExePath is null)
                _gitExePath = ResolveGitExe();
            return _gitExePath;
        }
    }
    private static string? _gitExePath;

    /// <summary>
    /// Environment every git call inherits. Public so a test can assert the
    /// never-prompt promise instead of trusting a comment.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NonInteractiveEnvironment { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_PAGER"] = "cat",
            ["GIT_ASKPASS"] = "echo",
        });

    public static Task<GitResult> RunAsync(string workingDirectory, params string[] args) =>
        RunAsync(workingDirectory, CancellationToken.None, args);

    public static Task<GitResult> RunAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] args) =>
        RunAsync(workingDirectory, DefaultTimeout, cancellationToken, args);

    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        params string[] args)
    {
        if (timeout <= TimeSpan.Zero)
            return new GitResult(-1, string.Empty, "git timed out: timeout must be greater than zero.");
        var startInfo = new ProcessStartInfo(GitExePath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var pair in NonInteractiveEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        // -C is explicit as well as WorkingDirectory: packaged WinUI launches
        // can inherit a different process directory from the repository path.
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(workingDirectory);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            return new GitResult(-1, string.Empty, $"git is not available ({GitExePath}).");
        }

        if (process is null)
        {
            return new GitResult(-1, string.Empty, "git could not be started.");
        }

        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(timeout);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await DrainAsync(outputTask, errorTask).ConfigureAwait(false);

                // The caller cancelled: that must reach Parallel.ForEachAsync as a
                // cancellation, not as a "git timed out" result it would ignore.
                cancellationToken.ThrowIfCancellationRequested();
                return new GitResult(-1, string.Empty, "git timed out.");
            }

            await DrainAsync(outputTask, errorTask).ConfigureAwait(false);
            return new GitResult(
                process.ExitCode,
                Normalize(await outputTask.ConfigureAwait(false)),
                (await errorTask.ConfigureAwait(false)).Trim());
        }
    }

    /// <summary>
    /// Normalises captured git output without destroying its meaning: line
    /// endings become <c>\n</c> (git on Windows emits CRLF for some commands) and
    /// trailing blank lines are dropped, but leading whitespace is kept — in
    /// porcelain output the first column is data.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join("\n", lines).TrimEnd('\n');
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already gone (or could not be killed): nothing to do.
        }
    }

    private static async Task DrainAsync(Task<string> output, Task<string> error)
    {
        try
        {
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch
        {
            // A killed process can close its pipes mid-read; the call already failed.
        }
    }

    /// <summary>
    /// Resolves the git executable once per process:
    /// <c>GitExeOverride</c> → <c>DEVDESK_GIT_EXE</c> environment variable → a
    /// common installed-Git location → <c>git</c> on PATH.
    /// </summary>
    private static string ResolveGitExe()
    {
        if (!string.IsNullOrWhiteSpace(_gitExeOverride)
            && File.Exists(_gitExeOverride))
            return _gitExeOverride;

        var env = Environment.GetEnvironmentVariable(DevDeskGitExeEnv);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        foreach (var candidate in InstalledGitCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "git";
    }
}
