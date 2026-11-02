using System;
using System.IO;

namespace DevDesk.Services;

/// <summary>
/// Guards against the template being destroyed by its own copy: the target
/// folder must not be inside the template root, and the template root must not
/// be inside the target. Both directions are checked because a malicious or
/// mistyped path could nest either inside the other.
/// </summary>
public static class TemplateSafetyGuard
{
    /// <summary>
    /// Validates that <paramref name="targetPath"/> is safe to write into given
    /// the template at <paramref name="templateRoot"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When the target is inside the template, or the template is inside the
    /// target (either direction would let the copy touch its own source).
    /// </exception>
    public static void ValidateTargetOutsideTemplate(string targetPath, string templateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateRoot);

        var target = Path.GetFullPath(targetPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var template = Path.GetFullPath(templateRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Target inside template: copying would recurse into its own source.
        if (IsUnder(target, template))
        {
            throw new ArgumentException(
                $"Target '{target}' is inside the template '{template}'. " +
                "Scaffolding must write outside the template so the template stays untouched.",
                nameof(targetPath));
        }

        // Template inside target: a later sweep of the target could delete the
        // template's own files, or a re-scan could treat the template as a project.
        if (IsUnder(template, target))
        {
            throw new ArgumentException(
                $"Template '{template}' is inside the target '{target}'. " +
                "Choose a location that does not contain the template folder.",
                nameof(targetPath));
        }
    }

    private static bool IsUnder(string path, string parent)
    {
        return path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, parent, StringComparison.OrdinalIgnoreCase);
    }
}
