using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class IgnoreRuleMatcherTests
{
    [Theory]
    [InlineData("bin", true)]
    [InlineData("src\\obj", true)]
    [InlineData("node_modules\\lib", true)]
    [InlineData("Source\\Visible", false)]
    [InlineData(".hidden\\Project", true)]
    [InlineData(".git\\objects", true)]
    public void Matcher_AppliesDefaultAndBuiltInRules(string path, bool expected)
    {
        var matcher = new IgnoreRuleMatcher(PathHygiene.IgnoredFolderNames);

        Assert.Equal(expected, matcher.IsExcluded(path));
    }

    [Fact]
    public void Matcher_AppliesOrderedNegationToConfigurableRules()
    {
        var matcher = new IgnoreRuleMatcher(new[] { "build", "!build/keep", "build/keep/private" });

        Assert.True(matcher.IsExcluded("build/output"));
        Assert.False(matcher.IsExcluded("build/keep"));
        Assert.True(matcher.IsExcluded("build/keep/private"));
    }

    [Fact]
    public void Matcher_CannotNegateGitMetadata()
    {
        var matcher = new IgnoreRuleMatcher(new[] { ".git", "!.git" });

        Assert.True(matcher.IsExcluded("repo/.git"));
        Assert.True(matcher.IsExcluded("repo/.git/objects"));
    }

    [Fact]
    public void Matcher_PreviewUsesTheSameOrderedDecisionsAsScanning()
    {
        var matcher = new IgnoreRuleMatcher(new[] { "build", "!build/keep", "build/keep/private" });
        var preview = matcher.Preview(new[] { "build/output", "build/keep", "build/keep/private", "repo/.git" });

        Assert.Equal(preview.Select(item => item.IsExcluded), new[]
        {
            matcher.IsExcluded("build/output"),
            matcher.IsExcluded("build/keep"),
            matcher.IsExcluded("build/keep/private"),
            matcher.IsExcluded("repo/.git"),
        });
        Assert.Equal("!build/keep", preview[1].MatchingRule);
        Assert.True(preview[3].IsSafetyExcluded);
    }

    [Theory]
    [InlineData("*.user", "project.csproj.user", true)]
    [InlineData("*.user", "project.userless\\nested", false)]
    [InlineData("**\\obj", "src\\app\\obj", true)]
    [InlineData("src/*.tmp", "src\\file.tmp", true)]
    [InlineData("src/*.tmp", "src\\nested\\file.tmp", false)]
    public void Matcher_MatchesNormalizedGlobPaths(string rule, string path, bool expected)
    {
        var matcher = new IgnoreRuleMatcher(new[] { rule });

        Assert.Equal(expected, matcher.IsExcluded(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("!")]
    [InlineData("../outside")]
    [InlineData("C:\\temp")]
    public void Matcher_RejectsInvalidRules(string rule) =>
        Assert.False(IgnoreRuleMatcher.IsValidPattern(rule));
}
