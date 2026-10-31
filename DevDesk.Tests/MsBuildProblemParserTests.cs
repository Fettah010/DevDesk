using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The parser reads MSBuild's frame, not its prose, because the SDK is localized
/// and the message is not. These cases are the real shapes the tools emit, plus
/// the near-misses that must stay ordinary output: a parser that invents a
/// diagnostic produces a row nobody can click and a failure nobody can find.
/// </summary>
public class MsBuildProblemParserTests
{
    [Fact]
    public void Parse_ReadsTheFullFrameWithProjectContext()
    {
        var problem = MsBuildProblemParser.ParseLine(
            @"C:\Code\DevEcosystem\Formixa\MainWindow.xaml.cs(12,5): error CS1002: ; expected [C:\Code\DevEcosystem\Formixa\Formixa.csproj]");

        Assert.NotNull(problem);
        Assert.Equal(BuildProblemSeverity.Error, problem!.Severity);
        Assert.Equal("CS1002", problem.Code);
        Assert.Equal(@"C:\Code\DevEcosystem\Formixa\MainWindow.xaml.cs", problem.File);
        Assert.Equal(12, problem.Line);
        Assert.Equal(5, problem.Column);
        Assert.Equal("; expected", problem.Message);
        Assert.Equal(@"C:\Code\DevEcosystem\Formixa\Formixa.csproj", problem.Project);
    }

    [Fact]
    public void Parse_ReadsAWarningWithNoColumn()
    {
        var problem = MsBuildProblemParser.ParseLine(
            @"MainWindow.xaml.cs(7): warning CS8618: Non-nullable field must contain a non-null value");

        Assert.NotNull(problem);
        Assert.Equal(BuildProblemSeverity.Warning, problem!.Severity);
        Assert.Equal("CS8618", problem.Code);
        Assert.Equal(7, problem.Line);
        Assert.Null(problem.Column);
        Assert.False(problem.IsError);
    }

    [Fact]
    public void Parse_KeepsAPathWithSpacesWhole()
    {
        var problem = MsBuildProblemParser.ParseLine(
            @"C:\Code\DevEcosystem\DevTem WinUI 3\App.xaml.cs(3,1): error CS0103: The name 'x' does not exist [C:\Code\DevEcosystem\DevTem WinUI 3\App.csproj]");

        Assert.NotNull(problem);
        Assert.Equal(@"C:\Code\DevEcosystem\DevTem WinUI 3\App.xaml.cs", problem!.File);
        Assert.Equal(@"C:\Code\DevEcosystem\DevTem WinUI 3\App.csproj", problem.Project);
    }

    [Fact]
    public void Parse_ReadsATaskErrorWithNoFile()
    {
        var problem = MsBuildProblemParser.ParseLine("CSC : error CS2001: Source file 'x.cs' could not be found.");

        Assert.NotNull(problem);
        Assert.Equal(BuildProblemSeverity.Error, problem!.Severity);
        Assert.Equal("CS2001", problem.Code);
        Assert.False(problem.HasFile);
        Assert.Equal("CSC", problem.Project);
        Assert.Contains("could not be found", problem.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReadsABareErrorWithoutATool()
    {
        var problem = MsBuildProblemParser.ParseLine("error NU1101: Unable to find package Foo.");

        Assert.NotNull(problem);
        Assert.Equal("NU1101", problem!.Code);
        Assert.Equal("Unable to find package Foo.", problem.Message);
    }

    [Fact]
    public void Parse_ReadsAMsBuildTaskError()
    {
        var problem = MsBuildProblemParser.ParseLine("MSBUILD : error MSB1009: Project file does not exist.");

        Assert.NotNull(problem);
        Assert.Equal("MSB1009", problem!.Code);
        Assert.Equal("MSBUILD", problem.Project);
    }

    [Fact]
    public void Parse_IgnoresOrdinaryOutput()
    {
        Assert.Null(MsBuildProblemParser.ParseLine("Build succeeded."));
        Assert.Null(MsBuildProblemParser.ParseLine("  Determining projects to restore..."));
        Assert.Null(MsBuildProblemParser.ParseLine("Time Elapsed 00:00:01.23"));
        Assert.Null(MsBuildProblemParser.ParseLine(string.Empty));
        Assert.Null(MsBuildProblemParser.ParseLine(null));
    }

    [Fact]
    public void Parse_DoesNotInventADiagnosticFromALocalizedWord()
    {
        // The message is translated; the "file(line,col): error CODE:" frame is not.
        Assert.Null(MsBuildProblemParser.ParseLine("Fehler CS1002: ; erwartet"));
        Assert.Null(MsBuildProblemParser.ParseLine("erreur CS1002 : ; attendu"));
    }

    [Fact]
    public void Collector_KeepsOneRowPerDiagnostic()
    {
        var collector = new BuildProblemCollector();
        const string line = @"A.cs(1,1): error CS1002: ; expected [C:\A.csproj]";

        collector.Add(line);
        collector.Add(line);

        Assert.Single(collector.Problems);
        Assert.Equal(1, collector.ErrorCount);
    }

    [Fact]
    public void Collector_FirstErrorIsTheFirstErrorNotTheFirstProblem()
    {
        var collector = new BuildProblemCollector();

        collector.Add(@"A.cs(1,1): warning CS8618: possible null [C:\A.csproj]");
        collector.Add(@"A.cs(2,1): error CS1002: ; expected [C:\A.csproj]");
        collector.Add(@"A.cs(3,1): error CS0103: name missing [C:\A.csproj]");

        Assert.Equal(1, collector.WarningCount);
        Assert.Equal(2, collector.ErrorCount);
        Assert.Contains("CS1002", collector.FirstError, System.StringComparison.Ordinal);
        Assert.DoesNotContain("CS0103", collector.FirstError!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Collector_MergesAWrappedMessageIntoThePreviousProblem()
    {
        var collector = new BuildProblemCollector();

        collector.Add(@"A.cs(1,1): error CS1503: Argument 1: cannot convert from 'string' [C:\A.csproj]");
        collector.Add("    to 'int'");

        Assert.Single(collector.Problems);
        Assert.Contains("to 'int'", collector.Problems[0].Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Collector_StopsAtTheLimitAndSaysItDid()
    {
        var collector = new BuildProblemCollector(limit: 2);

        collector.Add(@"A.cs(1,1): error CS1002: one");
        collector.Add(@"A.cs(2,1): error CS1002: two");
        collector.Add(@"A.cs(3,1): error CS1002: three");

        Assert.Equal(2, collector.Problems.Count);
        Assert.True(collector.Truncated);
    }

    [Fact]
    public void Problem_ToDiagnosticTextIsCopyable()
    {
        var problem = new BuildProblem(
            BuildProblemSeverity.Error,
            "CS1002",
            @"C:\Code\A.cs",
            12,
            5,
            "; expected",
            @"C:\Code\A.csproj");

        Assert.Equal(@"C:\Code\A.cs(12,5): error CS1002: ; expected", problem.ToDiagnosticText());
        Assert.Equal("A.cs:12:5", problem.Location);
    }
}
