using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DevDesk.Services;

/// <summary>
/// The token map for a scaffolded app: every literal the template hardcodes
/// under its own name, and what it becomes in the copy.
/// <para>
/// Tokens are applied as plain string replacements (not regex), so a token that
/// appears in C# namespaces, csproj elements, XAML x:Class attributes, window
/// titles and solution project entries all get replaced consistently.
/// </para>
/// </summary>
public sealed class TokenMap
{
    private static readonly Regex InvalidIdentifierChars =
        new("[^a-zA-Z0-9_]", RegexOptions.Compiled);

    /// <summary>The name the user typed, e.g. "My New App".</summary>
    public string AppName { get; }

    /// <summary>Identifier-safe form of <see cref="AppName"/>, e.g. "MyNewApp".</summary>
    public string SafeName { get; }

    /// <summary>C# root namespace / assembly name, same as <see cref="SafeName"/>.</summary>
    public string RootNamespace => SafeName;

    /// <summary>Assembly name, same as <see cref="SafeName"/>.</summary>
    public string AssemblyName => SafeName;

    /// <summary>Executable file name, e.g. "MyNewApp.exe".</summary>
    public string ExeName => $"{SafeName}.exe";

    /// <summary>Manifest app identity, e.g. "MyNewApp.app".</summary>
    public string AppId => $"{SafeName}.app";

    /// <summary>NuGet package title.</summary>
    public string PackTitle => AppName;

    /// <summary>NuGet package authors.</summary>
    public string PackAuthors { get; }

    /// <summary>Repository URL, empty when the template has no remote.</summary>
    public string RepoUrl { get; }

    /// <summary>Window subtitle, same as <see cref="AppName"/>.</summary>
    public string WindowSubtitle => AppName;

    /// <summary>Version to stamp into the new csproj, e.g. "0.1.0".</summary>
    public string Version { get; }

    /// <summary>Four-part form of <see cref="Version"/> for AssemblyVersion/FileVersion.</summary>
    public string Version4Part => Version.EndsWith(".0")
        ? Version[..^2]
        : $"{Version}.0";

    /// <summary>Every old→new substitution, in application order.</summary>
    public IReadOnlyList<(string Old, string New)> Substitutions { get; }

    private TokenMap(
        string appName,
        string safeName,
        string packAuthors,
        string repoUrl,
        string version)
    {
        AppName = appName;
        SafeName = safeName;
        PackAuthors = packAuthors;
        RepoUrl = repoUrl;
        Version = version;

        Substitutions = new List<(string Old, string New)>
        {
            // The template's PascalCase name appears in namespaces, class names,
            // csproj elements and solution entries.
            ("DevTemWinUi3", safeName),
            // The display title appears in the window Title and manifest.
            ("DevTem-WinUI 3", appName),
            // The folder-style name appears in paths and comments.
            ("DevTem WinUI 3", appName),
            // Short version appears in <Version> and <InformationalVersion>.
            ("1.0.0", version),
            // Four-part version appears in <AssemblyVersion> and <FileVersion>.
            ("1.0.0.0", Version4Part),
        };
    }

    /// <summary>
    /// Builds a token map for scaffolding <paramref name="appName"/> from the
    /// template at <paramref name="templatePath"/>.
    /// </summary>
    /// <param name="appName">The name the user typed (may contain spaces).</param>
    /// <param name="packAuthors">NuGet package authors; defaults to "DevDesk".</param>
    /// <param name="repoUrl">Repository URL copied from the template's remote.</param>
    /// <param name="version">Version to stamp; defaults to "0.1.0".</param>
    public static TokenMap Create(
        string appName,
        string? packAuthors = null,
        string? repoUrl = null,
        string? version = null)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new ArgumentException("App name is required.", nameof(appName));
        }

        var safe = MakeSafeName(appName.Trim());
        if (safe.Length == 0)
        {
            throw new ArgumentException(
                "App name must contain at least one identifier character.",
                nameof(appName));
        }

        return new TokenMap(
            appName.Trim(),
            safe,
            packAuthors ?? "DevDesk",
            repoUrl ?? string.Empty,
            version ?? "0.1.0");
    }

    /// <summary>Applies every substitution to <paramref name="text"/>.</summary>
    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        foreach (var (old, newValue) in Substitutions)
        {
            result = result.Replace(old, newValue, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Turns a user-typed name into an identifier-safe form: keeps only
    /// letters, digits and underscores, and forces the first character to be
    /// a letter or underscore.
    /// </summary>
    private static string MakeSafeName(string name)
    {
        var cleaned = InvalidIdentifierChars.Replace(name, string.Empty);
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        var first = cleaned[0];
        if (!char.IsLetter(first) && first != '_')
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }
}
