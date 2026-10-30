using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DevDesk.Tests;

/// <summary>
/// Runs a repository script with Windows PowerShell / pwsh and captures its
/// output. Used by the tests that assert on the release and version tooling;
/// every call goes through an argument list (no shell interpolation).
/// </summary>
internal static class PowerShellRunner
{
    /// <summary>Result of one script run.</summary>
    public sealed record RunResult(int ExitCode, string StdOut, string StdErr)
    {
        public string All => StdOut + Environment.NewLine + StdErr;
    }

    /// <summary>
    /// Locates a usable shell. Prefers the one bundled with Windows so tests do
    /// not depend on PATH; returns null when the machine has none.
    /// </summary>
    public static string? FindShell()
    {
        var bundled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "-NoProfile -Command \"exit 0\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (probe is not null && probe.WaitForExit(5000) && probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Win32Exception)
            {
                // not on PATH
            }
        }

        return null;
    }

    /// <summary>
    /// Runs <paramref name="scriptPath"/> with <paramref name="arguments"/>.
    /// Returns null when no shell exists (the caller skips; the suite stays
    /// green on a machine without PowerShell instead of reporting a false fail).
    /// </summary>
    public static RunResult? Run(string scriptPath, params string[] arguments)
    {
        var shell = FindShell();
        if (shell is null)
        {
            return null;
        }

        var psi = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoLayout.Root,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {shell}.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new RunResult(process.ExitCode, stdout, stderr);
    }
}
