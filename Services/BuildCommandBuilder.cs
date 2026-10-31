using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// Builds the argument LIST for every build verb (W-0.3.3).
/// <para>
/// Pure by design: the exact list a click produces is asserted in a test instead
/// of being observed in a running process. Nothing here ever concatenates a
/// command string for execution — <see cref="Preview"/> exists for the screen and
/// the log, and is the only place a string is built.
/// </para>
/// </summary>
public static class BuildCommandBuilder
{
    public const string Dotnet = "dotnet";

    public static IReadOnlyList<string> BuildArguments(BuildProfile profile) =>
        Common("build", profile);

    public static IReadOnlyList<string> PublishArguments(BuildProfile profile) =>
        Common("publish", profile);

    public static IReadOnlyList<string> TestArguments(BuildProfile profile) =>
        Common("test", profile);

    /// <summary>
    /// The fallback when no built executable can be found (D-0.3.2). No
    /// <c>--no-build</c>: if we are here, there is nothing built to reuse.
    /// </summary>
    public static IReadOnlyList<string> RunArguments(BuildProfile profile)
    {
        var arguments = new List<string> { "run", "--project", profile.TargetPath, "-c", profile.Configuration };

        if (profile.PlatformApplies)
        {
            arguments.Add($"-p:Platform={profile.Platform}");
        }

        arguments.AddRange(profile.ExtraArguments);
        return arguments;
    }

    public static ProcessRequest BuildRequest(BuildProfile profile, TimeSpan? timeout = null) =>
        Request(profile, BuildArguments(profile), timeout);

    public static ProcessRequest PublishRequest(BuildProfile profile, TimeSpan? timeout = null) =>
        Request(profile, PublishArguments(profile), timeout);

    public static ProcessRequest TestRequest(BuildProfile profile, TimeSpan? timeout = null) =>
        Request(profile, TestArguments(profile), timeout);

    /// <summary>
    /// A running application has no timeout: the user stops it, or it exits. A
    /// build that "times out" after 30 minutes is a bug report; an app that is
    /// closed by a timer is worse.
    /// </summary>
    public static ProcessRequest RunRequest(BuildProfile profile) =>
        Request(profile, RunArguments(profile), TimeSpan.Zero);

    /// <summary>
    /// The literal command a user would type, quoted for reading only. Paths with
    /// spaces stay one visually quoted argument.
    /// </summary>
    public static string Preview(IEnumerable<string> arguments) =>
        Dotnet + " " + string.Join(' ', arguments.Select(Quote));

    public static string Preview(ProcessRequest request) =>
        request.FileName + " " + string.Join(' ', request.Arguments.Select(Quote));

    private static IReadOnlyList<string> Common(string verb, BuildProfile profile)
    {
        var arguments = new List<string> { verb, profile.TargetPath, "-c", profile.Configuration };

        if (profile.PlatformApplies)
        {
            arguments.Add($"-p:Platform={profile.Platform}");
        }

        if (profile.NoRestore)
        {
            arguments.Add("--no-restore");
        }

        arguments.AddRange(profile.ExtraArguments);
        return arguments;
    }

    private static ProcessRequest Request(BuildProfile profile, IReadOnlyList<string> arguments, TimeSpan? timeout) =>
        new(Dotnet, arguments, profile.ProjectPath, Environment: null, Timeout: timeout);

    private static string Quote(string argument) =>
        argument.Contains(' ') || argument.Contains('\t') ? $"\"{argument}\"" : argument;
}

/// <summary>
/// Where a Run would start (D-0.3.2). "Which project is the application?" is a
/// real question in a workspace-shaped repo, and answering it wrongly would mean
/// launching something the user did not ask for.
/// </summary>
public sealed record RunResolution(string ProjectFile, string AssemblyName, string? Reason)
{
    public bool CanRun => !string.IsNullOrEmpty(ProjectFile);

    public static RunResolution No(string reason) => new(string.Empty, string.Empty, reason);
}

public static class BuildOutputLocator
{
    /// <summary>
    /// Decides which project a Run would launch. A solution target is accepted
    /// only when the repository root holds exactly one project, and that project
    /// must produce an executable.
    /// </summary>
    public static RunResolution ResolveRunTarget(BuildProfile profile)
    {
        ProjectFileInfo? info;

        if (profile.TargetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            info = ProjectFileInfo.Read(profile.TargetPath);
            if (info is null)
            {
                return RunResolution.No("The project file could not be read.");
            }
        }
        else
        {
            var projects = SafeProjects(profile.ProjectPath);
            if (projects.Count == 0)
            {
                return RunResolution.No(
                    "No application project at the repository root — open the solution in Visual Studio to run a specific project.");
            }

            if (projects.Count > 1)
            {
                return RunResolution.No(
                    $"This solution has {projects.Count} projects at its root — open it in Visual Studio to choose what to run.");
            }

            info = ProjectFileInfo.Read(projects[0]);
            if (info is null)
            {
                return RunResolution.No("The project file could not be read.");
            }
        }

        if (!info.IsApplication)
        {
            return RunResolution.No("This project builds a library, not an application.");
        }

        return new RunResolution(info.Path, info.AssemblyName, null);
    }

    /// <summary>
    /// The newest executable the last build produced, or null when there is none
    /// (the caller then falls back to <c>dotnet run</c>).
    /// </summary>
    public static string? FindExecutable(BuildProfile profile, RunResolution run)
    {
        if (!run.CanRun)
        {
            return null;
        }

        var info = ProjectFileInfo.Read(run.ProjectFile);
        var binary = run.AssemblyName + ".exe";
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(info?.TargetFramework))
        {
            var framework = info!.TargetFramework!;
            candidates.Add(Path.Combine(profile.ProjectPath, "bin", profile.Platform, profile.Configuration, framework, binary));
            candidates.Add(Path.Combine(profile.ProjectPath, "bin", profile.Configuration, framework, binary));

            foreach (var runtime in SafeDirectories(Path.Combine(profile.ProjectPath, "bin", profile.Configuration, framework)))
            {
                candidates.Add(Path.Combine(runtime, binary));
            }
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Directory.Build.props can move the output folder, so the last resort is
        // a bounded search for the assembly under bin, newest first.
        return NewestUnder(Path.Combine(profile.ProjectPath, "bin"), binary);
    }

    private static string? NewestUnder(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(root, fileName, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> SafeProjects(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*.csproj").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
