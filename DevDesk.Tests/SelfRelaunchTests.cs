using System;
using System.IO;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// "Rebuild &amp; relaunch" is the one flow that must hand work to a process that
/// is not this one: Windows locks the running exe and dll, so a build started
/// from inside the app fails with MSB3027 (measured). These cases pin the two
/// halves of that handover — the exact argument list, and the wait living in the
/// launcher rather than in a comment.
/// </summary>
public class SelfRelaunchTests
{
    [Fact]
    public void ArgumentsFor_CarriesThePidAsItsOwnArgument()
    {
        var arguments = SelfRelaunch.ArgumentsFor(@"C:\repo\Scripts\launch-dev.ps1", 4242);

        Assert.Equal(
            new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-WindowStyle", "Hidden",
                "-File", @"C:\repo\Scripts\launch-dev.ps1",
                "-WaitForProcessId", "4242",
            },
            arguments);
    }

    [Fact]
    public void ArgumentsFor_KeepsAPathWithSpacesInOneArgument()
    {
        var path = @"C:\Code\DevEcosystem\DevTem WinUI 3\Scripts\launch-dev.ps1";
        var arguments = SelfRelaunch.ArgumentsFor(path, 7);

        Assert.Contains(path, arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains('"'));
        Assert.Equal(9, arguments.Count);
    }

    [Fact]
    public void LauncherPath_IsTheScriptTheShortcutRuns()
    {
        Assert.Equal(
            Path.Combine(@"C:\repo", "Scripts", "launch-dev.ps1"),
            SelfRelaunch.LauncherPath(@"C:\repo"));

        Assert.True(File.Exists(RepoLayout.Path("Scripts", "launch-dev.ps1")));
    }

    [Fact]
    public void TryStart_RefusesAMissingLauncherWithASentence()
    {
        var missingRoot = Path.Combine(
            Path.GetTempPath(),
            "devdesk-no-such-repo-" + Guid.NewGuid().ToString("N"));

        Assert.False(SelfRelaunch.TryStart(missingRoot, Environment.ProcessId, out var error));
        Assert.NotNull(error);
        Assert.Contains("not found", error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingRoot, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryStart_RefusesANonsensePid()
    {
        Assert.False(SelfRelaunch.TryStart(RepoLayout.Root, 0, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Launcher_WaitsForTheOldPidBeforeItBuilds()
    {
        var script = File.ReadAllText(RepoLayout.Path("Scripts", "launch-dev.ps1"));

        Assert.Contains("$WaitForProcessId", script, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(30000)", script, StringComparison.Ordinal);

        var wait = script.IndexOf("WaitForExit(30000)", StringComparison.Ordinal);
        var build = script.IndexOf("dotnet build DevDesk.csproj", StringComparison.Ordinal);

        Assert.True(wait > 0, "The launcher must bound the wait.");
        Assert.True(
            build > wait,
            "The launcher must wait for the old window before it builds over the locked exe.");
    }
}
