using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace DevDesk.Services;

/// <summary>
/// "Rebuild &amp; relaunch" (W-0.3.8).
/// <para>
/// The rebuild cannot happen inside this process: Windows locks the running
/// <c>DevDesk.exe</c> and <c>DevDesk.dll</c>, so <c>dotnet build</c> fails with
/// MSB3027 as soon as a source changed (measured against this repo). The launcher
/// therefore does the waiting — it is started with this process's id, waits for it
/// to exit, builds only if a source is newer than its stamp, and starts the app
/// through the normal single-instance flow. That is also why the app exits
/// immediately after starting it instead of holding the lock.
/// </para>
/// <para>
/// The command is an argument list, never an interpolated string (gotcha 7): the
/// script path and the pid travel as separate arguments, so a checkout path with
/// spaces cannot turn into extra shell words.
/// </para>
/// </summary>
public static class SelfRelaunch
{
    public const string PowerShell = "powershell.exe";

    /// <summary>Relative to the repository root; the desktop shortcut runs the same file.</summary>
    public const string LauncherRelativePath = @"Scripts\launch-dev.ps1";

    /// <summary>
    /// How long the launcher waits for the old window. Longer than a slow close,
    /// shorter than a user's patience; on expiry it starts nothing.
    /// </summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public static string LauncherPath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, LauncherRelativePath);

    /// <summary>
    /// The exact argument list the button produces: the shortcut's own flags plus
    /// the pid to wait for. A test asserts it, so a changed flag cannot silently
    /// stop the wait from happening.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsFor(string launcherPath, int processId) => new[]
    {
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-WindowStyle", "Hidden",
        "-File", launcherPath,
        "-WaitForProcessId", processId.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Starts the launcher and returns at once; the caller then closes the window.
    /// Every failure is a sentence for the user, never an exception.
    /// </summary>
    public static bool TryStart(string repositoryRoot, int processId, out string? error)
    {
        error = null;

        if (processId <= 0)
        {
            error = "This process has no id to hand over, so the relaunch was not started.";
            return false;
        }

        var launcher = LauncherPath(repositoryRoot);
        if (!File.Exists(launcher))
        {
            error = $"The launcher was not found at {launcher}.";
            return false;
        }

        var startInfo = new ProcessStartInfo(PowerShell)
        {
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
            CreateNoWindow = true,
        };

        foreach (var argument in ArgumentsFor(launcher, processId))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var started = Process.Start(startInfo);
            if (started is null)
            {
                error = "The launcher could not be started.";
                return false;
            }

            LoggingService.Log.Information(
                "Relaunch: launcher started for pid {Pid} (launcher pid {LauncherPid})",
                processId,
                started.Id);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            LoggingService.Log.Warning(ex, "Relaunch: could not start {Launcher}", launcher);
            error = $"The launcher could not be started: {ex.Message}";
            return false;
        }
    }
}
