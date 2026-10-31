using System;
using System.Collections.Generic;

namespace DevDesk.Models;

/// <summary>
/// Build defaults shared by every project (settings → <c>build</c>). A value here
/// is a starting point, never a lock: each project can override it and the
/// override wins.
/// </summary>
public sealed class BuildSettings
{
    public const int DefaultHistoryLimit = 200;

    public string Configuration { get; set; } = "Debug";

    public string Platform { get; set; } = "x64";

    public bool NoRestore { get; set; }

    /// <summary>Console behaviour: keep the newest line in view while output arrives.</summary>
    public bool FollowTail { get; set; } = true;

    public bool WrapLines { get; set; }

    public int HistoryLimit { get; set; } = DefaultHistoryLimit;
}

/// <summary>
/// One project's override (settings → <c>buildProfiles</c>, keyed by the
/// repository path). Null means "inherit the default" — that is different from an
/// explicit empty string, which is never stored.
/// </summary>
public sealed class BuildProfileSettings
{
    public string? Configuration { get; set; }

    public string? Platform { get; set; }

    /// <summary>Absolute path, or a path relative to the repository root.</summary>
    public string? Target { get; set; }

    public string ExtraArgs { get; set; } = string.Empty;

    public bool? NoRestore { get; set; }
}

/// <summary>
/// Everything one build needs, resolved from defaults + overrides + the folder
/// itself. Immutable: a profile is a decision already made, not a form.
/// </summary>
public sealed record BuildProfile(
    string ProjectPath,
    string TargetPath,
    string TargetName,
    string Configuration,
    string Platform,
    bool PlatformApplies,
    bool NoRestore,
    IReadOnlyList<string> ExtraArguments)
{
    public bool IsBuildable => !string.IsNullOrEmpty(TargetPath);

    /// <summary>"Debug · x64" — the platform is only shown when it is passed.</summary>
    public string Summary => PlatformApplies ? $"{Configuration} · {Platform}" : Configuration;
}

/// <summary>
/// A profile, or the reason this folder cannot have one. A reason is always
/// user-visible text: "nothing to build" is an answer, a disabled button is not.
/// </summary>
public sealed record BuildProfileResolution(BuildProfile? Profile, string? Reason, string? Warning = null)
{
    public bool IsBuildable => Profile is not null;

    public static BuildProfileResolution NotBuildable(string reason) => new(null, reason);
}
