using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// The parts of a .csproj the build surfaces need: what it produces, what it is
/// called, and whether it understands a platform.
/// <para>
/// Read as XML rather than matched with a regex so a commented-out property or a
/// conditional block cannot be mistaken for a real one; conditions are ignored on
/// purpose (evaluating MSBuild is the SDK's job, not ours).
/// </para>
/// </summary>
public sealed record ProjectFileInfo(
    string Path,
    string AssemblyName,
    string? TargetFramework,
    string OutputType,
    bool UsesWindowsAppSdk,
    IReadOnlyList<string> Platforms)
{
    public bool IsApplication =>
        string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns null when the file cannot be read or parsed.</summary>
    public static ProjectFileInfo? Read(string projectFile)
    {
        if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(projectFile, LoadOptions.None);
            var elements = document.Descendants().ToList();

            string? Value(string name) => elements
                .LastOrDefault(element => string.Equals(element.Name.LocalName, name, StringComparison.Ordinal))
                ?.Value
                .Trim();

            var platforms = Value("Platforms")?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? new List<string>();

            return new ProjectFileInfo(
                projectFile,
                Value("AssemblyName") ?? System.IO.Path.GetFileNameWithoutExtension(projectFile),
                Value("TargetFramework") ?? Value("TargetFrameworks")?.Split(';').FirstOrDefault()?.Trim(),
                Value("OutputType") ?? "Library",
                UsesWindowsAppSdk: elements.Any(element =>
                    element.Name.LocalName is "PackageReference" or "PackageVersion"
                    && string.Equals((string?)element.Attribute("Include"), "Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase))
                    || elements.Any(element => element.Name.LocalName == "UseWinUI"),
                platforms);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            LoggingService.Log.Warning(ex, "Could not read the project file {Path}", projectFile);
            return null;
        }
    }
}

/// <summary>
/// Turns defaults + a project override + the folder into one <see cref="BuildProfile"/>
/// (W-0.3.2). Pure apart from reading the marker and the project file, so every
/// rule here is testable without a build.
/// </summary>
public static class BuildProfileResolver
{
    public const string DefaultConfiguration = "Debug";

    /// <summary>The platforms this app knows how to pass; anything else falls back to x64.</summary>
    public static IReadOnlyList<string> KnownPlatforms { get; } = new[] { "x86", "x64", "ARM64", "AnyCPU" };

    /// <summary>
    /// Resolves a profile. <paramref name="findTarget"/> is injectable so a test can
    /// describe a folder without creating one; production passes
    /// <see cref="ProjectOpenActions.FindSolutionOrProject"/>.
    /// </summary>
    public static BuildProfileResolution Resolve(
        string projectPath,
        BuildSettings defaults,
        BuildProfileSettings? overrides = null,
        Func<string, string?>? findTarget = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
        {
            return BuildProfileResolution.NotBuildable("The project folder does not exist.");
        }

        var finder = findTarget ?? ProjectOpenActions.FindSolutionOrProject;
        string? warning = null;

        var target = ResolveTargetPath(projectPath, overrides?.Target, finder, out var overrideProblem);
        if (overrideProblem is not null)
        {
            warning = overrideProblem;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return BuildProfileResolution.NotBuildable(
                "No .NET solution or project marker in this folder — nothing to build here.");
        }

        var configuration = Clean(overrides?.Configuration)
            ?? Clean(defaults.Configuration)
            ?? DefaultConfiguration;

        var platform = NormalizePlatform(Clean(overrides?.Platform) ?? defaults.Platform);

        return new BuildProfileResolution(
            new BuildProfile(
                projectPath,
                target,
                System.IO.Path.GetFileName(target),
                configuration,
                platform,
                PlatformApplies(target, platform),
                overrides?.NoRestore ?? defaults.NoRestore,
                SplitArguments(overrides?.ExtraArgs)),
            Reason: null,
            Warning: warning);
    }

    /// <summary>
    /// An explicit platform is passed unless the target is a single project that
    /// does not use Windows App SDK and either enumerates its own
    /// <c>&lt;Platforms&gt;</c> without this value or enumerates none at all.
    /// Solutions are left alone: their projects decide, and a WinUI project in a
    /// solution needs the property even when the solution file knows nothing about it.
    /// </summary>
    public static bool PlatformApplies(string targetPath, string platform)
    {
        if (!targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var info = ProjectFileInfo.Read(targetPath);
        if (info is null || info.UsesWindowsAppSdk)
        {
            return true;
        }

        // A project that enumerates platforms decides; one that enumerates none
        // never asked for the property, so the profile does not invent it.
        return info.Platforms.Count > 0
            && info.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizePlatform(string? platform)
    {
        var cleaned = Clean(platform);
        if (cleaned is null)
        {
            return "x64";
        }

        var known = KnownPlatforms.FirstOrDefault(candidate =>
            string.Equals(candidate, cleaned, StringComparison.OrdinalIgnoreCase));

        return known ?? "x64";
    }

    /// <summary>
    /// Splits a user's extra-arguments line into an argument list. Double quotes
    /// group a value with spaces; everything else is a separator. The result is
    /// always a list, never a command string (AGENTS gotcha 7).
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in text)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    private static string? ResolveTargetPath(
        string projectPath,
        string? overrideTarget,
        Func<string, string?> finder,
        out string? problem)
    {
        problem = null;

        if (!string.IsNullOrWhiteSpace(overrideTarget))
        {
            var candidate = System.IO.Path.IsPathRooted(overrideTarget)
                ? overrideTarget
                : System.IO.Path.Combine(projectPath, overrideTarget);

            if (File.Exists(candidate))
            {
                return System.IO.Path.GetFullPath(candidate);
            }

            problem = $"The saved build target \"{overrideTarget}\" was not found; using the detected one.";
            LoggingService.Log.Warning("Build target override {Target} does not exist in {Project}", overrideTarget, projectPath);
        }

        return finder(projectPath);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
