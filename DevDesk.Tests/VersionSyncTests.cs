using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Guards the version contract (roadmap §4.2.3): the four fields in
/// <c>DevDesk.csproj</c> always move together.
/// </summary>
public class VersionSyncTests
{
    private static readonly Regex NumericVersion = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

    public static (string Version, string AssemblyVersion, string FileVersion, string InformationalVersion) ReadDeclaredVersion()
    {
        var doc = XDocument.Load(RepoLayout.Path("DevDesk.csproj"));
        string Get(string name) => doc.Descendants(name).FirstOrDefault()?.Value.Trim() ?? string.Empty;

        return (Get("Version"), Get("AssemblyVersion"), Get("FileVersion"), Get("InformationalVersion"));
    }

    [Fact]
    public void Version_IsSemanticThreePart()
    {
        var declared = ReadDeclaredVersion();

        Assert.Matches(NumericVersion, declared.Version);
    }

    [Fact]
    public void AssemblyAndFileVersion_FollowTheNumericVersion()
    {
        var declared = ReadDeclaredVersion();

        Assert.Equal($"{declared.Version}.0", declared.AssemblyVersion);
        Assert.Equal($"{declared.Version}.0", declared.FileVersion);
    }

    [Fact]
    public void InformationalVersion_CarriesTheNumericVersionAndKnownSuffix()
    {
        var declared = ReadDeclaredVersion();

        Assert.StartsWith(declared.Version, declared.InformationalVersion, StringComparison.Ordinal);

        var suffix = declared.InformationalVersion[declared.Version.Length..].TrimStart('-');
        Assert.True(
            suffix is "" or "beta" or "dev",
            $"InformationalVersion suffix must be empty, beta or dev (got '{suffix}').");
    }

    [Fact]
    public void RepositoryUrl_PointsAtDevDesk()
    {
        var doc = XDocument.Load(RepoLayout.Path("DevDesk.csproj"));
        var url = doc.Descendants("RepositoryUrl").FirstOrDefault()?.Value.Trim() ?? string.Empty;

        Assert.Contains("DevDesk", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RepoLayout.TemplateRepoName, url, StringComparison.OrdinalIgnoreCase);
    }
}
