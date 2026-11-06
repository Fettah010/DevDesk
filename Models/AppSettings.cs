using System;
using System.Collections.Generic;
using DevDesk.Services;

namespace DevDesk.Models;

/// <summary>One scan root: where to look, how deep, and what to skip inside it.</summary>
public sealed class ScanRootSettings
{
    public string Path { get; set; } = ScanEngine.DefaultScanRoot;

    public int Depth { get; set; } = ScanEngine.DefaultMaxDepth;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ordered folder/glob exclusions for this root. Safety exclusions such as
    /// Git metadata remain protected independently of these user rules.
    /// </summary>
    public List<string> Ignores { get; set; } = new(PathHygiene.IgnoredFolderNames);
}

/// <summary>Remembered window geometry. Applied from 0.1.9.</summary>
public sealed class WindowSettings
{
    public int X { get; set; } = 120;

    public int Y { get; set; } = 80;

    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 820;

    public bool Maximized { get; set; }
}

public sealed class DiagnosticsSettings
{
    public string LogLevel { get; set; } = "Information";

    public int KeepDays { get; set; } = 14;
}

public sealed class FeatureSettings
{
    public bool BackgroundHealthCheck { get; set; }

    public bool MinimizeToTray { get; set; }
}

/// <summary>
/// The settings document (roadmap §4.3, <c>schemaVersion: 1</c>).
/// <para>
/// Policy: additive keys only within a major version, unknown keys preserved on
/// save, and a schema bump requires a migration with a test. Everything here has
/// a safe default, so a missing or half-edited file still produces a working app.
/// </para>
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Theme { get; set; } = nameof(AppTheme.System);

    public string Channel { get; set; } = ReleaseChannels.Default;

    public string Platform { get; set; } = "x64";

    public List<ScanRootSettings> Roots { get; set; } = new() { new ScanRootSettings() };

    public string DefaultReleaseChannel { get; set; } = ReleaseChannels.Default;

    public WindowSettings Window { get; set; } = new();

    public string LastRoute { get; set; } = "dashboard";

    public DiagnosticsSettings Diagnostics { get; set; } = new();

    public FeatureSettings Features { get; set; } = new();

    /// <summary>Build defaults for every project (0.3.0).</summary>
    public BuildSettings Build { get; set; } = new();

    /// <summary>
    /// Per-project build overrides keyed by the repository's full path. The
    /// comparer makes a differently-cased path resolve to the same entry, which is
    /// how Windows paths behave everywhere else in the app.
    /// </summary>
    public Dictionary<string, BuildProfileSettings> BuildProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
