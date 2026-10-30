using System;
using System.IO;

namespace DevDesk.Services;

/// <summary>
/// Finds the DevDesk checkout the running app belongs to, so the DevDesk page
/// can show the source repo instead of a phantom card.
/// <para>
/// Discovery is by walking up from the executable for a <c>.git</c> entry, never
/// by looking for a folder called "DevDesk": renaming or moving the checkout
/// keeps working, and an installed build (which has no source) correctly
/// reports <c>null</c>.
/// </para>
/// </summary>
public static class SelfLocator
{
    public const string InstalledFolderName = "DevDesk";

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> and returns the first repo
    /// root, or null when there is none or the app is an installed build.
    /// </summary>
    public static string? FindSourceRepo(string? startDirectory) =>
        FindSourceRepo(
            startDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// Testable overload: <paramref name="localAppData"/> is explicit so the
    /// "installed" rule can be exercised without touching the real profile.
    /// </summary>
    public static string? FindSourceRepo(string? startDirectory, string? localAppData)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        string current;
        try
        {
            if (!Directory.Exists(startDirectory))
            {
                return null;
            }

            current = PathHygiene.Canonicalize(startDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        // An installed build lives under %LocalAppData%\DevDesk (Velopack's
        // current/ install root). Even if a stray .git exists there, it is not a
        // source checkout and must not be presented as one.
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var installedRoot = Path.Combine(localAppData, InstalledFolderName);
            if (PathHygiene.IsUnder(current, installedRoot))
            {
                return null;
            }
        }

        while (true)
        {
            if (PathHygiene.IsRepositoryRoot(current))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length)
            {
                return null; // drive root reached
            }

            current = parent;
        }
    }
}
