using System;
using System.Linq;
using System.Reflection;

namespace DevDesk.Services;

/// <summary>
/// The app's identity, read from the assembly rather than from a string
/// literal, so Settings/About and the updater cannot disagree with the csproj.
/// <para>
/// <c>InformationalVersion</c> is the only field that carries the channel
/// suffix (<c>0.1.0-beta</c>); the other three are numeric by contract.
/// Builds that append source-revision metadata (<c>+abc1234</c>) are handled by
/// <see cref="VersionInfo.Parse"/>.
/// </para>
/// </summary>
public sealed class AppInfo
{
    public static AppInfo Current { get; } = new();

    private AppInfo()
    {
        var assembly = typeof(AppInfo).Assembly;

        var raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? VersionInfo.Fallback;

        RawInformationalVersion = raw;

        var parsed = VersionInfo.Parse(raw);
        Version = parsed.Version;
        Suffix = parsed.Suffix;
        InformationalVersion = parsed.Display;

        RepositoryUrl = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "RepositoryUrl", StringComparison.Ordinal))
            ?.Value ?? string.Empty;

        var appVersion = assembly.GetName().Version;
        AssemblyVersion = appVersion is null ? "0.0.0.0" : appVersion.ToString();
    }

    /// <summary>Version with the channel suffix, e.g. <c>0.1.0-beta</c>.</summary>
    public string InformationalVersion { get; }

    /// <summary>Version exactly as compiled, including any <c>+build</c> metadata.</summary>
    public string RawInformationalVersion { get; }

    /// <summary>Numeric version, e.g. <c>0.1.0</c>.</summary>
    public string Version { get; }

    /// <summary>Channel suffix without the dash: <c>beta</c>, <c>dev</c>, or empty.</summary>
    public string Suffix { get; }

    public bool IsPrerelease => Suffix.Length > 0;

    public string DisplayVersion => InformationalVersion;

    public string VersionDisplay => $"Version {DisplayVersion}";

    /// <summary>Numeric assembly version, e.g. <c>0.1.0.0</c>.</summary>
    public string AssemblyVersion { get; }

    /// <summary>Repository URL emitted by the SDK's <c>&lt;RepositoryUrl&gt;</c>.</summary>
    public string RepositoryUrl { get; }
}
