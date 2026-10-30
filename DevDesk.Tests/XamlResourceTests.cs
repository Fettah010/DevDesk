using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Guards the theme-resource keys used by XAML. A misspelled or invented
/// <c>{ThemeResource …}</c> key is not a compile error: it throws
/// <c>XamlParseException</c> the moment the page is navigated to, which is how
/// GitHubPage crashed on every open (TextFillColorCriticalBrush /
/// TextFillColorSuccessBrush do not exist in WinUI's palette).
/// </summary>
public class XamlResourceTests
{
    private static readonly Regex ThemeResourceKey =
        new(@"\{ThemeResource\s+([A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled);

    /// <summary>The only members of WinUI's TextFillColor*Brush family.</summary>
    private static readonly HashSet<string> TextFillVariants = new(StringComparer.Ordinal)
    {
        "Primary", "Secondary", "Tertiary", "Disabled", "Inverse",
    };

    [Fact]
    public void Xaml_UsesOnlyRealTextFillColorBrushes()
    {
        var offenders = new List<string>();

        foreach (var file in XamlFiles())
        {
            var relative = Path.GetRelativePath(RepoLayout.Root, file);
            var lineNumber = 0;

            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                foreach (Match match in ThemeResourceKey.Matches(line))
                {
                    var key = match.Groups[1].Value;
                    if (!key.StartsWith("TextFillColor", StringComparison.Ordinal) ||
                        !key.EndsWith("Brush", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var variant = key["TextFillColor".Length..^"Brush".Length];
                    if (!TextFillVariants.Contains(variant))
                    {
                        offenders.Add($"{relative}:{lineNumber} -> {key}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Unknown TextFillColor theme resources (pages will crash on navigation):\n" +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Xaml_DefinesEverySeaBlueKeyItUses()
    {
        var appResources = File.ReadAllText(RepoLayout.Path("App.xaml"));
        var offenders = new List<string>();

        foreach (var file in XamlFiles())
        {
            foreach (Match match in ThemeResourceKey.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value;
                if (!key.StartsWith("SeaBlue", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!appResources.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoLayout.Root, file)} -> {key}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These SeaBlue keys are used but never defined in App.xaml: " +
            string.Join(", ", offenders.Distinct()));
    }

    private static IEnumerable<string> XamlFiles()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        return Directory
            .EnumerateFiles(RepoLayout.Root, "*.xaml", options)
            .Where(f => !Path.GetRelativePath(RepoLayout.Root, f)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".git" or ".vs"));
    }
}
