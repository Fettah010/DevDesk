using System.IO;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

[CollectionDefinition("Environment-mutating", DisableParallelization = true)]
public sealed class EnvironmentMutatingCollection
{
}

/// <summary>
/// The jump is best-effort, the location is not. These cases pin the order
/// (VS Code, then Visual Studio, then the default app) and the fact that the
/// line and column survive into the argument list instead of being folded into a
/// command string.
/// </summary>
[Collection("Environment-mutating")]
public class ProblemNavigatorTests
{
    private static BuildProblem Problem(int? line = 12, int? column = 5) =>
        new(
            BuildProblemSeverity.Error,
            "CS1002",
            @"C:\Code\DevEcosystem\DevTem WinUI 3\App.xaml.cs",
            line,
            column,
            "; expected",
            @"C:\Code\DevEcosystem\DevTem WinUI 3\App.csproj");

    [Fact]
    public void Choose_PrefersVsCodeAndKeepsThePathWithSpacesWhole()
    {
        var command = ProblemNavigator.Choose(Problem(), @"C:\Tools\code.cmd", @"C:\VS\devenv.exe");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Tools\code.cmd", command!.FileName);
        Assert.Equal(new[] { "--goto", @"C:\Code\DevEcosystem\DevTem WinUI 3\App.xaml.cs:12:5" }, command.Arguments);
    }

    [Fact]
    public void Choose_WithoutAColumnStillJumpsToTheLine()
    {
        var command = ProblemNavigator.Choose(Problem(column: null), @"C:\Tools\code.cmd", null);

        Assert.Equal(@"C:\Code\DevEcosystem\DevTem WinUI 3\App.xaml.cs:12:1", command!.Arguments[1]);
    }

    [Fact]
    public void Choose_WithoutALineOpensTheFile()
    {
        var command = ProblemNavigator.Choose(Problem(line: null, column: null), @"C:\Tools\code.cmd", null);

        Assert.Equal(@"C:\Code\DevEcosystem\DevTem WinUI 3\App.xaml.cs", command!.Arguments[1]);
    }

    [Fact]
    public void Choose_FallsBackToVisualStudio()
    {
        var command = ProblemNavigator.Choose(Problem(), codePath: null, devenvPath: @"C:\VS\devenv.exe");

        Assert.NotNull(command);
        Assert.Equal(@"C:\VS\devenv.exe", command!.FileName);
        Assert.Equal(new[] { "/edit", @"C:\Code\DevEcosystem\DevTem WinUI 3\App.xaml.cs" }, command.Arguments);
    }

    [Fact]
    public void Choose_WithoutAnEditorReturnsNullForTheDefaultApp()
    {
        Assert.Null(ProblemNavigator.Choose(Problem(), null, null));
    }

    [Fact]
    public void Choose_AProblemWithoutAFileCannotBeOpened()
    {
        var problem = new BuildProblem(BuildProblemSeverity.Error, "MSB1009", string.Empty, null, null, "missing", null);

        Assert.Null(ProblemNavigator.Choose(problem, @"C:\Tools\code.cmd", @"C:\VS\devenv.exe"));
    }

    [Fact]
    public void FindOnPath_FindsAnExistingToolAndIgnoresAMissingOne()
    {
        var folder = Path.GetTempPath();
        var name = "devdesk-tool-" + System.Guid.NewGuid().ToString("N") + ".exe";
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "not a real executable");

        try
        {
            var previous = System.Environment.GetEnvironmentVariable("PATH");
            System.Environment.SetEnvironmentVariable("PATH", folder);

            try
            {
                Assert.Equal(path, ProblemNavigator.FindOnPath(name));
                Assert.Null(ProblemNavigator.FindOnPath("devdesk-no-such-tool.exe"));
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("PATH", previous);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
