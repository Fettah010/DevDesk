using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

/// <summary>Outcome of one git process call.</summary>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Safe git process runner: argument LISTS (never interpolated shell strings —
/// see AGENTS gotcha #7), redirected output, and a hard timeout so a hung git
/// never freezes a scan. Never writes, never touches the network.
/// </summary>
public static class GitRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<GitResult> RunAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new GitResult(-1, string.Empty, "git is not available on PATH.");
        }

        if (process is null)
            return new GitResult(-1, string.Empty, "git could not be started.");

        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                using var timeout = new CancellationTokenSource(Timeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // process already gone
                }

                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
                return new GitResult(-1, string.Empty, "git timed out.");
            }

            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new GitResult(process.ExitCode, (await outputTask).Trim(), (await errorTask).Trim());
        }
    }
}