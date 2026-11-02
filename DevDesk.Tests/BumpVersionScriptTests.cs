using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Runs <c>Scripts/bump-version.ps1</c> against a temporary copy of the csproj.
/// Never touches the real project file; skips when no PowerShell is available.
/// </summary>
public class BumpVersionScriptTests
{
    private static readonly string ScriptPath = RepoLayout.Path("Scripts", "bump-version.ps1");

    [Fact]
    public void Bump_WritesAllFourFields_AndKeepsTheSuffixWhenOmitted()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");

        var exit = Run(temp.File, "0.2.0");

        Assert.Equal(0, exit);
        var fields = temp.Read();
        Assert.Equal("0.2.0", fields.Version);
        Assert.Equal("0.2.0.0", fields.AssemblyVersion);
        Assert.Equal("0.2.0.0", fields.FileVersion);
        Assert.Equal("0.2.0-beta", fields.InformationalVersion);
    }

    [Fact]
    public void Bump_RemovesTheSuffix_WhenAskedForNone()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");

        var exit = Run(temp.File, "1.0.0", suffix: "none");

        Assert.Equal(0, exit);
        Assert.Equal("1.0.0", temp.Read().InformationalVersion);
    }

    [Fact]
    public void Bump_NormalizesStableSuffixToCanonicalUnsuffixedVersion()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");

        var exit = Run(temp.File, "0.2.0", suffix: "stable");

        Assert.Equal(0, exit);
        Assert.Equal("0.2.0", temp.Read().InformationalVersion);
        Assert.Empty(temp.TemporaryArtifacts());
    }

    [Fact]
    public void Bump_IsIdempotent()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");

        Assert.Equal(0, Run(temp.File, "0.2.0", suffix: "beta"));
        var first = temp.Text();

        Assert.Equal(0, Run(temp.File, "0.2.0", suffix: "beta"));
        var second = temp.Text();

        Assert.Equal(first, second);
        Assert.Empty(temp.TemporaryArtifacts());
    }

    [Fact]
    public void Bump_RejectsMalformedVersion()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");
        var before = temp.Text();

        var exit = Run(temp.File, "1.2");

        Assert.NotEqual(0, exit);
        Assert.Equal(before, temp.Text());
    }

    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    public void Bump_RejectsLeadingZeroesWithoutChangingTheProject(string version)
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");
        var before = temp.Text();

        var exit = Run(temp.File, version);

        Assert.NotEqual(0, exit);
        Assert.Equal(before, temp.Text());
        Assert.Empty(temp.TemporaryArtifacts());
    }

    [Theory]
    [InlineData("2147483648.0.0")]
    [InlineData("0.2147483648.0")]
    [InlineData("0.0.2147483648")]
    public void Bump_RejectsComponentsOutsideTheSupportedRange(string version)
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");
        var before = temp.Text();

        var exit = Run(temp.File, version);

        Assert.NotEqual(0, exit);
        Assert.Equal(before, temp.Text());
        Assert.Empty(temp.TemporaryArtifacts());
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("nightly")]
    [InlineData("beta2")]
    public void Bump_RejectsUnsupportedChannelsWithoutChangingTheProject(string suffix)
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");
        var before = temp.Text();

        var exit = Run(temp.File, "0.2.0", suffix);

        Assert.NotEqual(0, exit);
        Assert.Equal(before, temp.Text());
        Assert.Empty(temp.TemporaryArtifacts());
    }

    [Fact]
    public void Bump_RejectsDuplicateVersionPropertyWithoutChangingTheProject()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");
        var before = temp.Text().Replace(
            "    <Version>0.1.0</Version>",
            "    <Version>0.1.0</Version>\n    <Version>0.1.1</Version>",
            StringComparison.Ordinal);
        temp.WriteText(before);

        var exit = Run(temp.File, "0.2.0");

        Assert.NotEqual(0, exit);
        Assert.Equal(before, temp.Text());
        Assert.Empty(temp.TemporaryArtifacts());
    }

    [Fact]
    public void Bump_RejectsMissingVersionFieldWithoutChangingTheProject()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");
        var before = temp.Text().Replace(
            "    <FileVersion>0.1.0.0</FileVersion>\n",
            string.Empty,
            StringComparison.Ordinal);
        temp.WriteText(before);

        var exit = Run(temp.File, "0.2.0");

        Assert.NotEqual(0, exit);
        Assert.Equal(before, temp.Text());
        Assert.Empty(temp.TemporaryArtifacts());
    }

    [Fact]
    public void Bump_PreservesUtf8BomAndCrLfLineEndings()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");
        var text = temp.Text().Replace("\n", "\r\n", StringComparison.Ordinal);
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        System.IO.File.WriteAllText(temp.File, text, encoding);

        var exit = Run(temp.File, "0.2.0");

        Assert.Equal(0, exit);
        Assert.Equal(
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble(),
            System.IO.File.ReadAllBytes(temp.File)[..3]);
        var updated = temp.Text();
        Assert.Contains("\r\n", updated);
        Assert.DoesNotContain("\n", updated.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.Equal("0.2.0-beta", temp.Read().InformationalVersion);
        Assert.Empty(temp.TemporaryArtifacts());
    }

    private static int Run(string projectFile, string version, string? suffix = null)
    {
        var arguments = new List<string>
        {
            "-Version", version,
            "-Project", projectFile,
        };
        if (suffix is not null)
        {
            arguments.Add("-Suffix");
            arguments.Add(suffix);
        }

        return PowerShellRunner.Run(ScriptPath, arguments.ToArray())?.ExitCode ?? 0;
    }

    private sealed class TempProject : IDisposable
    {
        private readonly string _dir =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devdesk-bump-" + Guid.NewGuid().ToString("N"));

        public TempProject() => Directory.CreateDirectory(_dir);

        public string File => System.IO.Path.Combine(_dir, "DevDesk.csproj");

        public void Write(string version, string suffix)
        {
            var info = string.IsNullOrEmpty(suffix) ? version : $"{version}-{suffix}";
            System.IO.File.WriteAllText(File,
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <PropertyGroup>\n" +
                $"    <Version>{version}</Version>\n" +
                $"    <AssemblyVersion>{version}.0</AssemblyVersion>\n" +
                $"    <FileVersion>{version}.0</FileVersion>\n" +
                $"    <InformationalVersion>{info}</InformationalVersion>\n" +
                "  </PropertyGroup>\n" +
                "</Project>\n");
        }

        public string Text() => System.IO.File.ReadAllText(File);

        public void WriteText(string text) => System.IO.File.WriteAllText(File, text);

        public string[] TemporaryArtifacts() =>
            Directory.GetFiles(_dir, ".DevDesk.csproj.*");

        public (string Version, string AssemblyVersion, string FileVersion, string InformationalVersion) Read()
        {
            var doc = XDocument.Load(File);
            string Get(string name) => doc.Descendants(name).FirstOrDefault()?.Value.Trim() ?? string.Empty;
            return (Get("Version"), Get("AssemblyVersion"), Get("FileVersion"), Get("InformationalVersion"));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
