using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class GitStatusParserTests
{
    [Fact]
    public void Parse_PreservesPathsAndParsesBranchState()
    {
        var output = "# branch.head main\n# branch.upstream origin/main\n# branch.ab +2 -1\0" +
            "1 .M N... 100644 100644 100644 abcdef0 abcdef1 folder/file with spaces.txt\0" +
            "? untracked ünicode.txt\0" +
            "? multi\nline name.txt\0";

        var status = GitStatusParser.Parse(output);

        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.Collection(status.Files,
            file => Assert.Equal("folder/file with spaces.txt", file.Path),
            file => Assert.Equal("untracked ünicode.txt", file.Path),
            file => Assert.Equal("multi\nline name.txt", file.Path));
    }

    [Fact]
    public void Parse_ConsumesRenameSourceAsFollowingNulField()
    {
        var output = "2 R. N... 100644 100644 100644 abcdef0 abcdef1 R100 new name.txt\0old name.txt\0";

        var entry = Assert.Single(GitStatusParser.Parse(output).Files);

        Assert.Equal("renamed", entry.Kind);
        Assert.Equal("new name.txt", entry.Path);
        Assert.Equal("old name.txt", entry.OriginalPath);
    }
}
