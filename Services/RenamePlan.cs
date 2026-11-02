using System;
using System.Collections.Generic;
using System.IO;

namespace DevDesk.Services;

/// <summary>
/// A plan to rename one file during scaffolding: the source path (inside the
/// template) and the target path (inside the copy).
/// <para>
/// The template ships as `DevTemWinUi3.csproj` / `DevTemWinUi3.sln`; the copy
/// becomes `${SafeName}.csproj` / `${SafeName}.sln`. Files whose names don't
/// carry the template name are copied as-is (no rename entry).
/// </para>
/// </summary>
public sealed record RenamePlan(
    string SourcePath,
    string TargetPath)
{
    /// <summary>The file name as it will appear in the target.</summary>
    public string TargetFileName => Path.GetFileName(TargetPath);

    /// <summary>Whether this file is actually being renamed (different names).</summary>
    public bool IsRenamed =>
        !string.Equals(Path.GetFileName(SourcePath), TargetFileName, StringComparison.Ordinal);
}

/// <summary>
/// Computes the rename plan for every file in the template.
/// </summary>
public static class RenamePlanner
{
    /// <summary>
    /// Builds a rename plan for <paramref name="sourceFile"/> relative to the
    /// template root, given a <see cref="TokenMap"/>. Returns null when the file
    /// is copied as-is (no rename).
    /// </summary>
    public static RenamePlan? Plan(
        string sourceFile,
        string templateRoot,
        string targetRoot,
        TokenMap tokens)
    {
        var relative = Path.GetRelativePath(templateRoot, sourceFile);
        var fileName = Path.GetFileName(sourceFile);

        // Only csproj / sln files are renamed: they carry the template name and
        // must match the new assembly name. Everything else is copied as-is.
        var ext = Path.GetExtension(sourceFile);
        if (!string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var targetFileName = $"{tokens.SafeName}{ext}";
        var targetPath = Path.Combine(targetRoot, relative[..^fileName.Length], targetFileName);

        return new RenamePlan(sourceFile, targetPath);
    }
}
