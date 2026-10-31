using System;
using System.IO;

namespace DevDesk.Tests;

/// <summary>
/// A throw-away folder shaped like a repository: a solution and/or projects with
/// the properties the build surfaces read. Nothing here ever points at a real
/// repository, so a rule about markers can be tested by describing the folder
/// instead of finding one.
/// </summary>
internal sealed class TempProject : IDisposable
{
    public TempProject()
    {
        Root = Path.Combine(Path.GetTempPath(), "devdesk-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string AddSolution(string name = "App")
    {
        var path = Path.Combine(Root, name + ".sln");
        File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00");
        return path;
    }

    public string AddSolutionX(string name = "App")
    {
        var path = Path.Combine(Root, name + ".slnx");
        File.WriteAllText(path, "<Solution />");
        return path;
    }

    public string AddProject(
        string name = "App",
        string outputType = "WinExe",
        string targetFramework = "net10.0-windows10.0.19041.0",
        string? platforms = null,
        bool windowsAppSdk = true,
        string? assemblyName = null)
    {
        var path = Path.Combine(Root, name + ".csproj");
        var platformLine = platforms is null ? string.Empty : $"    <Platforms>{platforms}</Platforms>\n";
        var sdkLine = windowsAppSdk
            ? "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.WindowsAppSDK\" Version=\"2.6.4\" />\n  </ItemGroup>\n"
            : string.Empty;
        var assemblyLine = assemblyName is null ? string.Empty : $"    <AssemblyName>{assemblyName}</AssemblyName>\n";

        File.WriteAllText(
            path,
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>{outputType}</OutputType>\n    <TargetFramework>{targetFramework}</TargetFramework>\n{assemblyLine}{platformLine}  </PropertyGroup>\n{sdkLine}</Project>\n");

        return path;
    }

    /// <summary>Creates the executable a successful build would have produced.</summary>
    public string AddBuildOutput(
        string assemblyName = "App",
        string configuration = "Debug",
        string platform = "x64",
        string targetFramework = "net10.0-windows10.0.19041.0")
    {
        var folder = Path.Combine(Root, "bin", platform, configuration, targetFramework);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, assemblyName + ".exe");
        File.WriteAllText(path, "not a real executable");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
