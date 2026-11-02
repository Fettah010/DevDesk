using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class GitHubApiClientTests
{
    private static readonly GitHubRepositoryIdentity Identity =
        new("github.com", "owner", "repo");

    [Fact]
    public async Task GetRepository_SendsExpectedHeadersAndConditionalEtag()
    {
        using var handler = new ScriptedHandler(_ => JsonResponse(
            """{"full_name":"owner/repo","html_url":"https://github.com/owner/repo","private":false}""",
            HttpStatusCode.OK,
            "\"etag-1\""));
        using var http = new HttpClient(handler);
        var client = new GitHubApiClient(http);

        var response = await client.GetRepositoryAsync(Identity, "synthetic-token", "\"etag-0\"");

        Assert.True(response.Succeeded);
        Assert.Equal("owner/repo", response.Data!.FullName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.github.com", request.RequestUri!.Host);
        Assert.Equal("/repos/owner/repo", request.RequestUri.AbsolutePath);
        Assert.Equal("synthetic-token", request.AuthorizationParameter);
        Assert.Contains("application/vnd.github+json", request.Accept);
        Assert.Equal(GitHubApiClient.ApiVersion, request.ApiVersion);
        Assert.Equal("\"etag-0\"", request.IfNoneMatch);
        Assert.Contains("DevDesk", request.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRepository_MapsNotModifiedWithoutInventingAResponseBody()
    {
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        using var http = new HttpClient(handler);

        var response = await new GitHubApiClient(http)
            .GetRepositoryAsync(Identity, null, "\"old\"");

        Assert.True(response.Succeeded);
        Assert.True(response.NotModified);
        Assert.Null(response.Data);
        Assert.Null(response.Json);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitHubErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, GitHubErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, GitHubErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, GitHubErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, GitHubErrorKind.Server)]
    public async Task GetRepository_MapsHttpFailures(
        HttpStatusCode status,
        GitHubErrorKind expected)
    {
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(status));
        using var http = new HttpClient(handler);

        var response = await new GitHubApiClient(http).GetRepositoryAsync(Identity, null);

        Assert.Equal(expected, response.ErrorKind);
        Assert.False(response.Succeeded);
        Assert.DoesNotContain("synthetic-token", response.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForbiddenWithRetryAfterIsRateLimited()
    {
        using var handler = new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            response.Headers.Add("X-RateLimit-Limit", "5000");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1793500000");
            return response;
        });
        using var http = new HttpClient(handler);

        var response = await new GitHubApiClient(http).GetRepositoryAsync(Identity, null);

        Assert.Equal(GitHubErrorKind.RateLimited, response.ErrorKind);
        Assert.Equal(0, response.RateLimit!.Remaining);
        Assert.Equal(5000, response.RateLimit.Limit);
        Assert.Equal(TimeSpan.FromMinutes(2), response.RateLimit.RetryAfter);
        Assert.NotNull(response.RateLimit.ResetsAt);
    }

    [Fact]
    public async Task MalformedAndOversizedResponsesAreInvalid()
    {
        using var malformedHandler = new ScriptedHandler(_ => JsonResponse("{", HttpStatusCode.OK));
        using var malformedHttp = new HttpClient(malformedHandler);
        var malformed = await new GitHubApiClient(malformedHttp).GetRepositoryAsync(Identity, null);

        using var largeHandler = new ScriptedHandler(_ =>
        {
            var content = new ByteArrayContent(new byte[GitHubApiClient.MaximumCachedPayloadBytes + 1]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var largeHttp = new HttpClient(largeHandler);
        var oversized = await new GitHubApiClient(largeHttp).GetRepositoryAsync(Identity, null);

        Assert.Equal(GitHubErrorKind.InvalidResponse, malformed.ErrorKind);
        Assert.Equal(GitHubErrorKind.InvalidResponse, oversized.ErrorKind);
    }

    [Fact]
    public async Task TimeoutIsMappedAndCallerCancellationIsPreserved()
    {
        using var timeoutHandler = new ScriptedHandler(_ => throw new OperationCanceledException());
        using var timeoutHttp = new HttpClient(timeoutHandler);
        var timedOut = await new GitHubApiClient(timeoutHttp).GetRepositoryAsync(Identity, null);

        using var cancellationHandler = new ScriptedHandler(_ => JsonResponse("{}", HttpStatusCode.OK));
        using var cancellationHttp = new HttpClient(cancellationHandler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GitHubApiClient(cancellationHttp).GetRepositoryAsync(Identity, null, cancellationToken: cancellation.Token));

        Assert.Equal(GitHubErrorKind.TimedOut, timedOut.ErrorKind);
        Assert.Single(cancellationHandler.Requests);
    }

    [Fact]
    public async Task EveryEndpointUsesGetAndStaysOnGithubApiHost()
    {
        using var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? """{"workflow_runs":[]}"""
                : path.EndsWith("/check-runs", StringComparison.Ordinal)
                    ? """{"check_runs":[]}"""
                    : path.EndsWith("/pulls", StringComparison.Ordinal) || path.EndsWith("/releases", StringComparison.Ordinal)
                        ? "[]"
                        : path.EndsWith("/user", StringComparison.Ordinal)
                            ? """{"login":"test-user"}"""
                            : """{"full_name":"owner/repo","html_url":"https://github.com/owner/repo","private":false}""";
            return JsonResponse(body, HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var client = new GitHubApiClient(http);

        await client.GetAuthenticatedUserAsync("synthetic-token");
        await client.GetRepositoryAsync(Identity, null);
        await client.GetReleasesAsync(Identity, null);
        await client.GetWorkflowRunsAsync(Identity, null);
        await client.GetOpenPullRequestsAsync(Identity, null);
        await client.GetCheckRunsAsync(Identity, new string('a', 40), null);

        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
        });
    }

    [Fact]
    public async Task GetWorkflowRuns_DeserializesWorkflowPathAndTriggerEventForExactMatching()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string workflowPath = ".github/workflows/release.yml@refs/tags/v0.6.1-beta";
        using var handler = new ScriptedHandler(_ => JsonResponse(
            $$"""
            {
              "workflow_runs": [{
                "id": 42,
                "name": "Release",
                "run_number": 7,
                "head_branch": "v0.6.1-beta",
                "head_sha": "{{sha}}",
                "status": "in_progress",
                "conclusion": null,
                "html_url": "https://github.com/owner/repo/actions/runs/42",
                "created_at": "2026-11-02T00:00:00Z",
                "run_started_at": "2026-11-02T00:00:01Z",
                "updated_at": "2026-11-02T00:00:02Z",
                "path": "{{workflowPath}}",
                "event": "push"
              }]
            }
            """,
            HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        var response = await new GitHubApiClient(http).GetWorkflowRunsAsync(Identity, null);

        Assert.True(response.Succeeded);
        var run = Assert.Single(response.Data!.Runs);
        Assert.Equal(42, run.Id);
        Assert.Equal(sha, run.HeadSha);
        Assert.Equal("v0.6.1-beta", run.Branch);
        Assert.Equal(workflowPath, run.Path);
        Assert.Equal("push", run.Event);
    }

    [Fact]
    public async Task RepositoryPathRejectsHostOrPathInjection()
    {
        var enterprise = await Assert.ThrowsAsync<ArgumentException>(() =>
            new GitHubApiClient(new HttpClient(new ScriptedHandler(_ => JsonResponse("{}", HttpStatusCode.OK))))
                .GetRepositoryAsync(new GitHubRepositoryIdentity("github.enterprise.test", "owner", "repo"), null));
        var traversal = await Assert.ThrowsAsync<ArgumentException>(() =>
            new GitHubApiClient(new HttpClient(new ScriptedHandler(_ => JsonResponse("{}", HttpStatusCode.OK))))
                .GetRepositoryAsync(new GitHubRepositoryIdentity("github.com", "..", "repo"), null));

        Assert.Contains("github.com", enterprise.Message, StringComparison.Ordinal);
        Assert.Contains("invalid owner", traversal.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode status,
        string? etag = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (etag is not null)
        {
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.Parameter,
                string.Join(",", request.Headers.Accept.Select(header => header.MediaType)),
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions) ? versions.Single() : null,
                request.Headers.IfNoneMatch.SingleOrDefault()?.ToString(),
                string.Join(" ", request.Headers.UserAgent)));
            return Task.FromResult(respond(request));
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri? RequestUri,
        string? AuthorizationParameter,
        string Accept,
        string? ApiVersion,
        string? IfNoneMatch,
        string UserAgent);
}
