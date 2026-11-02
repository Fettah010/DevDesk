using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class GitHubRepositoryIdentityParserTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "github.com", "owner", "repo", true)]
    [InlineData("https://github.com/owner/repo", "github.com", "owner", "repo", true)]
    [InlineData("HTTPS://GITHUB.COM/Owner/Repo.GIT/", "github.com", "Owner", "Repo", true)]
    [InlineData("ssh://git@github.com/owner/repo.git", "github.com", "owner", "repo", true)]
    [InlineData("ssh://github.com/owner/repo", "github.com", "owner", "repo", true)]
    [InlineData("git@github.com:owner/repo.git", "github.com", "owner", "repo", true)]
    [InlineData("github.com:owner/repo", "github.com", "owner", "repo", true)]
    [InlineData("https://git.example.test/team/project.git", "git.example.test", "team", "project", false)]
    [InlineData("git@git.example.test:team/project.git", "git.example.test", "team", "project", false)]
    [InlineData("ssh://git@git.example.test/team/project", "git.example.test", "team", "project", false)]
    public void Parse_RecognizesSupportedRemoteShapes(
        string remote,
        string expectedHost,
        string expectedOwner,
        string expectedRepository,
        bool expectedApiSupport)
    {
        var identity = GitHubRepositoryIdentityParser.Parse(remote);

        Assert.NotNull(identity);
        Assert.Equal(expectedHost, identity.Host);
        Assert.Equal(expectedOwner, identity.Owner);
        Assert.Equal(expectedRepository, identity.Repository);
        Assert.Equal(expectedApiSupport, identity.SupportsPublicApi);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\projects\repo")]
    [InlineData("file:///C:/projects/repo")]
    [InlineData("https://github.com/owner/repo/tree/main")]
    [InlineData("https://github.com/owner/repo?token=secret")]
    [InlineData("https://user@github.com/owner/repo")]
    [InlineData("ssh://git@github.com:2222/owner/repo")]
    [InlineData("https://github.com/owner%2Frepo")]
    public void Parse_RejectsUnsupportedOrAmbiguousRemote(string? remote)
    {
        Assert.Null(GitHubRepositoryIdentityParser.Parse(remote));
    }

    [Fact]
    public void Parse_DoesNotMapEnterpriseHostToPublicApi()
    {
        var identity = GitHubRepositoryIdentityParser.Parse(
            "https://github.enterprise.example/organization/repository.git");

        Assert.NotNull(identity);
        Assert.Equal("github.enterprise.example", identity.Host);
        Assert.False(identity.SupportsPublicApi);
    }
}
