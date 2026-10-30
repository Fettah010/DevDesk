using System;

namespace DevDesk.Services;

/// <summary>
/// Pure parser for the <c>InformationalVersion</c> string
/// (<c>0.1.0-beta</c>, <c>0.1.0-beta+build</c>, <c>1.0.0</c>).
/// Kept separate from <see cref="AppInfo"/> so the parsing rules are unit
/// testable without loading a specific assembly version.
/// </summary>
public readonly record struct VersionInfo(string Version, string Suffix)
{
    public const string Fallback = "0.0.0";

    public bool IsPrerelease => Suffix.Length > 0;

    public string Display => IsPrerelease ? $"{Version}-{Suffix}" : Version;

    /// <summary>
    /// Never throws: a hand-edited or truncated value degrades to
    /// <see cref="Fallback"/> so the About surface can always render.
    /// </summary>
    public static VersionInfo Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new VersionInfo(Fallback, string.Empty);
        }

        var value = raw.Trim();

        // SourceLink/CI appends "+<commit>" metadata; it is not part of the version.
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var versionPart = dash >= 0 ? value[..dash] : value;
        var suffix = dash >= 0 ? value[(dash + 1)..].Trim() : string.Empty;

        if (!System.Version.TryParse(versionPart, out var parsed) || parsed.Major < 0)
        {
            return new VersionInfo(Fallback, string.Empty);
        }

        var normalized = $"{parsed.Major}.{Math.Max(0, parsed.Minor)}.{Math.Max(0, Math.Max(0, parsed.Build))}";
        return new VersionInfo(normalized, suffix);
    }
}
