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
    public void Bump_IsIdempotent()
    {
        using var temp = new TempProject();
        temp.Write(version: "0.1.0", suffix: "beta");

        Assert.Equal(0, Run(temp.File, "0.2.0", suffix: "beta"));
        var first = temp.Text();

        Assert.Equal(0, Run(temp.File, "0.2.0", suffix: "beta"));
        var second = temp.Text();

        Assert.Equal(first, second);
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
