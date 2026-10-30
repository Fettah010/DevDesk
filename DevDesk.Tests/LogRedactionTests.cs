using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// Redaction is a security promise (roadmap §6.5.2), so it is tested with
/// literal strings rather than "probably fine" patterns — and with ordinary log
/// text too, because a rule that mangles a path is its own defect.
/// </summary>
public class LogRedactionTests
{
    [Fact]
    public void Redaction_ScrubsCredentialsAndTokens()
    {
        const string urlWithPassword = "git clone https://owner:hunter2@github.com/owner/repo.git";
        var redactedUrl = LogRedaction.Redact(urlWithPassword);

        Assert.DoesNotContain("hunter2", redactedUrl, System.StringComparison.Ordinal);
        Assert.Contains("https://***:***@github.com", redactedUrl, System.StringComparison.Ordinal);

        const string urlWithToken = "remote https://ghp_16C7e42F292c6912E7710c838347Ae178B4a@github.com/owner/repo";
        var redactedToken = LogRedaction.Redact(urlWithToken);

        Assert.DoesNotContain("16C7e42F292c6912E7710c838347Ae178B4a", redactedToken, System.StringComparison.Ordinal);
        Assert.Contains("https://***@github.com", redactedToken, System.StringComparison.Ordinal);

        const string query = "GET https://api.github.com/repos?access_token=abc123&page=2";
        var redactedQuery = LogRedaction.Redact(query);

        Assert.DoesNotContain("abc123", redactedQuery, System.StringComparison.Ordinal);
        Assert.Contains("access_token=***", redactedQuery, System.StringComparison.Ordinal);
        Assert.Contains("page=2", redactedQuery, System.StringComparison.Ordinal); // non-secret params stay

        const string header = "Authorization: Bearer abcdef1234567890xyz";
        var redactedHeader = LogRedaction.Redact(header);

        Assert.DoesNotContain("abcdef1234567890xyz", redactedHeader, System.StringComparison.Ordinal);
        Assert.Contains("Bearer ***", redactedHeader, System.StringComparison.Ordinal);

        const string pat = "using github_pat_11ABCDEFG0123456789_abcdefghijklmnop";
        var redactedPat = LogRedaction.Redact(pat);

        Assert.DoesNotContain("11ABCDEFG0123456789", redactedPat, System.StringComparison.Ordinal);
        Assert.Contains("github_pat_***", redactedPat, System.StringComparison.Ordinal);

        const string bare = "token ghp_abcDEF1234567890abcDEF1234567890";
        var redactedBare = LogRedaction.Redact(bare);

        Assert.DoesNotContain("abcDEF1234567890abcDEF1234567890", redactedBare, System.StringComparison.Ordinal);
        Assert.Contains("ghp_***", redactedBare, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_LeavesOrdinaryTextAlone()
    {
        foreach (var text in new[]
                 {
                     "Catalog scan #1 finished in 380 ms: 5 project(s), self DevDesk",
                     @"Scanning C:\Code\projects\DevEcosystem · depth 2 · excludes DevDesk",
                     "DevTem WinUI 3 is behind upstream",
                     "git status --porcelain=v2 --branch",
                     "https://github.com/Fettah010/DevDesk",
                 })
        {
            Assert.Equal(text, LogRedaction.Redact(text));
        }
    }

    [Fact]
    public void Redaction_HandlesEmptyInput()
    {
        Assert.Equal(string.Empty, LogRedaction.Redact(null));
        Assert.Equal(string.Empty, LogRedaction.Redact(string.Empty));
    }
}
