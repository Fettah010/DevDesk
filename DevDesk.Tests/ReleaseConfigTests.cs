using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Release configuration is code too: these tests read the real repository
/// files and fail if the feed, the scripts or the workflow drift back to the
/// template, a second repo slug, an unpinned CLI, or a channel invented
/// somewhere else.
/// </summary>
public class ReleaseConfigTests
{
    private static string TemplateRepo => RepoLayout.TemplateRepoName;

    private static readonly string[] SourceExtensions =
    {
        "*.cs", "*.xaml", "*.csproj", "*.props", "*.targets", "*.ps1", "*.yml", "*.yaml"
    };

    private static readonly string[] SkippedDirectories =
    {
        "bin", "obj", ".git", ".vs", "Releases", "Logs", "docs"
    };

    [Fact]
    public void Feed_FollowsRepositoryUrl()
    {
        var declared = VersionSyncTests.ReadDeclaredVersion();
        Assert.False(string.IsNullOrWhiteSpace(declared.Version));

        var feed = UpdateService.Current.FeedUrl;

        Assert.False(string.IsNullOrWhiteSpace(feed));
        Assert.Equal(AppInfo.Current.RepositoryUrl, feed);
        Assert.Contains("DevDesk", feed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultChannel_IsBetaFromTheSharedVocabulary()
    {
        var service = UpdateService.Current;
        var original = service.Channel;

        try
        {
            Assert.Equal(ReleaseChannels.Default, original);
            Assert.Equal(ReleaseChannels.Beta, original);

            service.Channel = "not-a-channel";
            Assert.Equal(ReleaseChannels.Default, service.Channel);
        }
        finally
        {
            service.Channel = original;
        }
    }

    [Fact]
    public void Source_NoTemplateReferences()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (File.ReadAllText(file).Contains(TemplateRepo, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(Path.GetRelativePath(RepoLayout.Root, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "These files still reference the template repo: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Scripts_ShareOneRepoSlug()
    {
        foreach (var script in new[] { "build-and-release.ps1", "upload-github.ps1" })
        {
            var text = File.ReadAllText(RepoLayout.Path("Scripts", script));

            Assert.Contains("RepositoryUrl", text, StringComparison.Ordinal);
            Assert.DoesNotContain("https://github.com/", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Workflow_IsTagDriven_AndUsesThePinnedTool()
    {
        var text = File.ReadAllText(RepoLayout.Path(".github", "workflows", "release.yml"));

        Assert.Contains("-Tag", text, StringComparison.Ordinal);
        Assert.Contains("dotnet tool restore", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet tool install --global", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolsManifest_PinsVpk()
    {
        var manifest = RepoLayout.Path(".config", "dotnet-tools.json");
        Assert.True(File.Exists(manifest), "The pinned vpk manifest is missing: " + manifest);

        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var root = document.RootElement;

        Assert.True(root.GetProperty("isRoot").GetBoolean());
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        var vpk = root.GetProperty("tools").GetProperty("vpk");
        Assert.Equal("1.2.161", vpk.GetProperty("version").GetString());

        var commands = vpk.GetProperty("commands").EnumerateArray().Select(c => c.GetString()).ToArray();
        Assert.Contains("vpk", commands);
    }

    [Fact]
    public void Release_PackVersionMatchesCsproj()
    {
        var declared = VersionSyncTests.ReadDeclaredVersion();
        var expected = ExpectedChannel(declared.InformationalVersion);

        var result = PowerShellRunner.Run(RepoLayout.Path("Scripts", "build-and-release.ps1"), "-CheckOnly");

        if (result is null)
        {
            return; // no PowerShell here: the script is exercised on the dev machine/CI
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"version={declared.Version}", result.StdOut, StringComparison.Ordinal);
        Assert.Contains($"channel={expected}", result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-Version", "9.9.9")]
    [InlineData("-Tag", "v9.9.9-beta")]
    [InlineData("-Tag", "v0.1.0-alpha")]
    public void Release_RefusesAVersionOrChannelTheAppDoesNotReport(string flag, string value)
    {
        var result = PowerShellRunner.Run(
            RepoLayout.Path("Scripts", "build-and-release.ps1"),
            "-CheckOnly",
            flag,
            value);

        if (result is null)
        {
            return;
        }

        Assert.NotEqual(0, result.ExitCode);
    }

    private static string ExpectedChannel(string informationalVersion)
    {
        var dash = informationalVersion.IndexOf('-');
        var suffix = dash >= 0 ? informationalVersion[(dash + 1)..] : string.Empty;
        return ReleaseChannels.FromSuffix(suffix) ?? "unknown";
    }

    /// <summary>Every source/script/config file, minus build output and docs.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var pattern in SourceExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(RepoLayout.Root, pattern, options))
            {
                var relative = Path.GetRelativePath(RepoLayout.Root, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(SkippedDirectories.Contains))
                {
                    continue;
                }

                yield return file;
            }
        }
    }
}
