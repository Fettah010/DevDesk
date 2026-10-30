using System;
using System.IO;

namespace DevDesk.Tests;

/// <summary>
/// Locates the repository root from the test assembly location, so tests that
/// must read project files (csproj, scripts, docs) never hardcode a path.
/// </summary>
internal static class RepoLayout
{
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return System.IO.Path.Combine(all);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "DevDesk.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root (DevDesk.csproj) above " + AppContext.BaseDirectory);
    }
}
