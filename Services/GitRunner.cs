using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

/// <summary>Outcome of one git process call.</summary>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Safe git process runner: argument LISTS (never interpolated shell strings —
/// see AGENTS gotcha #7), redirected output, a hard timeout, and cancellation.
/// Never writes, never touches the network.
/// <para>
/// Every process runs non-interactively (see <see cref="NonInteractiveEnvironment"/>):
/// a scan that stops on a credential prompt would hang the whole app, and the
/// user is never at a terminal to answer it.
/// </para>
/// </summary>
public static class GitRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

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

    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
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
            return new GitResult(-1, string.Empty, "git is not available on PATH.");
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
                limit.CancelAfter(DefaultTimeout);
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
}
