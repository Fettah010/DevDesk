using System;
using System.IO;

namespace DevDesk.Models;

/// <summary>
/// A detected template repository: a folder that contains the marker files
/// required by the scaffold engine (csproj, App.xaml, Assets/app.ico,
/// Scripts/build-and-release.ps1). The name/description/version come from the
/// template's own csproj. This is read-only data; the template is never mutated.
/// </summary>
public sealed class TemplateDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string FullPath { get; init; }

    /// <summary>Short display name: the template folder name (e.g. "DevTem WinUI 3").</summary>
    public string DisplayName => Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public override string ToString() => $"Template({Name} v{Version} @ {FullPath})";
}