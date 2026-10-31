using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>One launch decision, kept separate from launching so it can be tested.</summary>
public sealed record ProblemOpenCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Opens a compiler problem at its file and line (W-0.3.5, D-0.3.7).
/// <para>
/// The order is fixed: <c>code --goto file:line:col</c>, then Visual Studio
/// (<c>devenv /edit file</c>), then the file's default app. The jump is
/// best-effort; the row keeps showing <c>file:line:col</c> and can be copied, so
/// a machine without a line-aware editor loses the jump, never the location.
/// </para>
/// </summary>
public static class ProblemNavigator
{
    /// <summary>
    /// The command to run, or null when the fallback (the default app) is the
    /// only option. Pure apart from the two paths the caller found.
    /// </summary>
    public static ProblemOpenCommand? Choose(BuildProblem problem, string? codePath, string? devenvPath)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (!problem.HasFile)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(codePath))
        {
            var target = problem.Line is { } line
                ? $"{problem.File}:{line}:{problem.Column ?? 1}"
                : problem.File;

            return new ProblemOpenCommand(codePath, new[] { "--goto", target });
        }

        if (!string.IsNullOrWhiteSpace(devenvPath))
        {
            return new ProblemOpenCommand(devenvPath, new[] { "/edit", problem.File });
        }

        return null;
    }

    /// <summary>Opens the problem. Returns false only when nothing could be started.</summary>
    public static bool Open(BuildProblem problem)
    {
        var command = Choose(problem, FindOnPath("code.cmd") ?? FindOnPath("code.exe"), FindOnPath("devenv.exe"));

        if (command is not null && TryStart(command))
        {
            return true;
        }

        return ProjectOpenActions.OpenWithDefaultApp(problem.File);
    }

    /// <summary>Finds an executable in PATH without starting a shell.</summary>
    public static string? FindOnPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed PATH entry is skipped, not fatal.
            }
        }

        return null;
    }

    private static bool TryStart(ProblemOpenCommand command)
    {
        try
        {
            var info = new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in command.Arguments)
            {
                info.ArgumentList.Add(argument);
            }

            _ = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LoggingService.Log.Warning(ex, "Could not open the problem with {File}", command.FileName);
            return false;
        }
    }
}
