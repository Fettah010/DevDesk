using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DevDesk.Models;

public sealed record GitHubUser(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("name")] string? Name);

public sealed record GitHubRepository(
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("private")] bool IsPrivate,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool IsPrerelease,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset> Assets);

public sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("size")] long Size);

public sealed record GitHubWorkflowRuns(
    [property: JsonPropertyName("workflow_runs")] IReadOnlyList<GitHubWorkflowRun> Runs);

public sealed record GitHubWorkflowRun(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("run_number")] int RunNumber,
    [property: JsonPropertyName("head_branch")] string? Branch,
    [property: JsonPropertyName("head_sha")] string? HeadSha,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("run_started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt)
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }
}

public sealed record GitHubPullRequest(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("head")] GitHubPullRequestHead? Head);

public sealed record GitHubPullRequestHead(
    [property: JsonPropertyName("sha")] string? Sha);

public sealed record GitHubPullRequestSummary(
    GitHubPullRequest PullRequest,
    string ChecksSummary);

public sealed record GitHubCheckRuns(
    [property: JsonPropertyName("check_runs")] IReadOnlyList<GitHubCheckRun> Runs);

public sealed record GitHubCheckRun(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion);

public sealed record GitHubRateLimit(
    int? Limit,
    int? Remaining,
    DateTimeOffset? ResetsAt,
    TimeSpan? RetryAfter = null);

public enum GitHubErrorKind
{
    None,
    NoCredential,
    Authentication,
    Forbidden,
    NotFound,
    RateLimited,
    Network,
    TimedOut,
    InvalidResponse,
    Server,
}

public sealed record GitHubDataResult<T>(
    T? Data,
    GitHubErrorKind ErrorKind,
    string? Message,
    bool IsFromCache,
    bool IsStale,
    DateTimeOffset? FetchedAt,
    GitHubRateLimit? RateLimit)
{
    public bool Succeeded => ErrorKind == GitHubErrorKind.None && Data is not null;
}
