using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DevDesk.Services;

/// <summary>
/// Shell-level "open" actions for Phase 0 cards. Everything is launched with
/// argument lists or ShellExecute (never interpolated shell strings — AGENTS
/// gotcha #7), and opening never throws: a broken/missing target returns false.
/// </summary>
public static class ProjectOpenActions
{
    private static readonly string[] TopLevelPatterns = { "*.sln", "*.slnx", "*.csproj", "*.vcxproj", "*.fsproj", "*.vbproj" };

    private static readonly string[] IgnoredNames =
    {
        "bin", "obj", ".git", ".vs", ".vscode", "node_modules", "packages",
        "Releases", "ReleasesLocal", "Logs", "dist", "build",
    };

    /// <summary>
    /// Best candidate to open in Visual Studio: a solution at the repo root,
    /// else a project marker at the root, else any project marker a couple of
    /// levels down (workspace-shaped repos). Prefers root markers, then slnx
    /// before csproj. Returns null when there is nothing to open.
    /// </summary>
    public static string? FindSolutionOrProject(string repoPath)
    {
        if (!Directory.Exists(repoPath))
        {
            return null;
        }

        var topLevel = new List<string>();
        foreach (var pattern in TopLevelPatterns)
        {
            topLevel.AddRange(SafeGetFiles(repoPath, pattern));
        }

        // Root .sln/.slnx first; then root project files.
        foreach (var file in topLevel)
        {
            if (file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        foreach (var file in topLevel)
        {
            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return DeepFind(repoPath, 3);
    }

    /// <summary>Opens a folder in Windows Explorer (argument-list safe).</summary>
    public static bool OpenInExplorer(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(folderPath);
            _ = Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens Windows Explorer with <paramref name="filePath"/> selected.</summary>
    public static bool RevealInExplorer(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            // /select,<path> is one argument to explorer; the argument list keeps
            // a path with spaces intact (AGENTS gotcha #7).
            var psi = new ProcessStartInfo("explorer.exe");
            psi.ArgumentList.Add($"/select,{filePath}");
            _ = Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens a file with its default handler (e.g. .sln → Visual Studio).</summary>
    public static bool OpenWithDefaultApp(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? DeepFind(string dir, int depth)
    {
        if (depth <= 0)
        {
            return null;
        }

        foreach (var sub in SafeGetDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name is null || IgnoredNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name[0] == '.')
            {
                continue;
            }

            foreach (var pattern in TopLevelPatterns)
            {
                var hit = SafeGetFiles(sub, pattern).FirstOrDefault();
                if (hit is not null)
                {
                    return hit;
                }
            }

            var deep = DeepFind(sub, depth - 1);
            if (deep is not null)
            {
                return deep;
            }
        }

        return null;
    }

    private static string[] SafeGetFiles(string dir, string pattern)
    {
        try
        {
            return Directory.GetFiles(dir, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string[] SafeGetDirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}