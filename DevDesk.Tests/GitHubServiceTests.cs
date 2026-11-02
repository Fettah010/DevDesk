using System;
using System.Collections.Concurrent;
using System.IO;
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

public class GitHubServiceTests
{
    private static readonly GitHubRepositoryIdentity Identity =
        new("github.com", "Owner", "Repo");

    [Fact]
    public async Task FreshCacheAvoidsNetworkAndExpiredCacheRevalidatesWithEtag()
    {
        using var sandbox = new GitHubSandbox();
        var clock = new TestClock();
        var calls = 0;
        using var handler = new ScriptedHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                return JsonResponse(RepositoryJson, HttpStatusCode.OK, "\"v1\"");
            }

            Assert.Equal("\"v1\"", request.Headers.IfNoneMatch.Single().Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, clock);

        var first = await service.GetRepositoryAsync(Identity);
        var fresh = await service.GetRepositoryAsync(Identity);
        clock.Advance(TimeSpan.FromMinutes(6));
        var revalidated = await service.GetRepositoryAsync(Identity);

        Assert.Equal(2, calls);
        Assert.False(first.IsFromCache);
        Assert.True(fresh.IsFromCache);
        Assert.False(fresh.IsStale);
        Assert.True(revalidated.IsFromCache);
        Assert.False(revalidated.IsStale);
        Assert.Equal("Owner/Repo", revalidated.Data!.FullName);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task NetworkFailureReturnsExplicitStaleCache()
    {
        using var sandbox = new GitHubSandbox();
        var clock = new TestClock();
        var calls = 0;
        using var handler = new ScriptedHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                return JsonResponse(RepositoryJson, HttpStatusCode.OK, "\"v1\"");
            }

            throw new HttpRequestException("offline");
        });
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, clock);

        await service.GetRepositoryAsync(Identity);
        clock.Advance(TimeSpan.FromMinutes(6));
        var result = await service.GetRepositoryAsync(Identity, forceRefresh: true);

        Assert.True(result.Succeeded);
        Assert.True(result.IsFromCache);
        Assert.True(result.IsStale);
        Assert.Contains("could not be reached", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotModifiedResponseWithoutCacheIsReportedAsInvalid()
    {
        using var sandbox = new GitHubSandbox();
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, new TestClock());

        var result = await service.GetRepositoryAsync(Identity);

        Assert.Equal(GitHubErrorKind.InvalidResponse, result.ErrorKind);
        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task ExpiredCacheCanBeRenderedBeforeARefreshRequest()
    {
        using var sandbox = new GitHubSandbox();
        var clock = new TestClock();
        using var handler = new ScriptedHandler(_ => JsonResponse(RepositoryJson, HttpStatusCode.OK, "\"v1\""));
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, clock);
        await service.GetRepositoryAsync(Identity);
        clock.Advance(TimeSpan.FromMinutes(6));

        var cached = service.GetCachedRepository(Identity);

        Assert.NotNull(cached);
        Assert.True(cached.IsStale);
        Assert.Equal("Owner/Repo", cached.Data!.FullName);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotFallBackToPrivateStaleCache()
    {
        using var sandbox = new GitHubSandbox(token: "synthetic-token");
        var clock = new TestClock();
        var calls = 0;
        using var handler = new ScriptedHandler(_ =>
        {
            calls++;
            return calls == 1
                ? JsonResponse(RepositoryJson, HttpStatusCode.OK, "\"v1\"")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, clock);

        await service.GetRepositoryAsync(Identity);
        var result = await service.GetRepositoryAsync(Identity, forceRefresh: true);

        Assert.Equal(GitHubErrorKind.Authentication, result.ErrorKind);
        Assert.Null(result.Data);
        Assert.False(result.IsStale);
    }

    [Fact]
    public async Task CacheNeverContainsTokenAndSignOutPurgesCachedData()
    {
        using var sandbox = new GitHubSandbox(token: "synthetic-token-never-persist");
        using var handler = new ScriptedHandler(_ => JsonResponse(RepositoryJson, HttpStatusCode.OK, "\"v1\""));
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, new TestClock());

        await service.GetRepositoryAsync(Identity);
        var cacheBefore = File.ReadAllText(sandbox.CachePath);
        Assert.DoesNotContain("synthetic-token-never-persist", cacheBefore, StringComparison.Ordinal);
        Assert.Contains("Owner/Repo", cacheBefore, StringComparison.Ordinal);

        await service.SignOutAsync();

        Assert.False(service.HasToken);
        var cacheAfter = File.ReadAllText(sandbox.CachePath);
        Assert.DoesNotContain("Owner/Repo", cacheAfter, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token-never-persist", cacheAfter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignOutDeletesCredentialEvenWhenCacheCannotBePurged()
    {
        using var sandbox = new GitHubSandbox(token: "synthetic-token");
        Directory.CreateDirectory(sandbox.CachePath);
        using var handler = new ScriptedHandler(_ => JsonResponse("{}", HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, new TestClock());

        await Assert.ThrowsAsync<IOException>(() => service.SignOutAsync());

        Assert.False(service.HasToken);
    }

    [Fact]
    public async Task TokenIsStoredOnlyAfterSuccessfulIdentityValidation()
    {
        using var sandbox = new GitHubSandbox();
        using var handler = new ScriptedHandler(_ => JsonResponse(
            """{"login":"copilot-user"}""",
            HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, new TestClock());

        var result = await service.SaveAndValidateTokenAsync("synthetic-token");

        Assert.True(result.Succeeded);
        Assert.Equal("copilot-user", result.Data!.Login);
        Assert.Equal("synthetic-token", sandbox.Credentials.Token);
        Assert.DoesNotContain("synthetic-token", File.ReadAllText(sandbox.CachePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidTokenIsNotStored()
    {
        using var sandbox = new GitHubSandbox();
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, new TestClock());

        var result = await service.SaveAndValidateTokenAsync("synthetic-token");

        Assert.False(result.Succeeded);
        Assert.Equal(GitHubErrorKind.Authentication, result.ErrorKind);
        Assert.Null(sandbox.Credentials.Token);
    }

    [Fact]
    public async Task ConcurrentColdReadsShareOneRequest()
    {
        using var sandbox = new GitHubSandbox();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref count);
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return JsonResponse(RepositoryJson, HttpStatusCode.OK, "\"v1\"");
        });
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, new TestClock());

        var firstTask = service.GetRepositoryAsync(Identity);
        var secondTask = service.GetRepositoryAsync(Identity);
        await entered.Task;
        release.SetResult(true);
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, count);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConstructionMakesNoGitHubRequests()
    {
        using var sandbox = new GitHubSandbox();
        using var handler = new ScriptedHandler(_ => JsonResponse("{}", HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        _ = sandbox.CreateService(http, new TestClock());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void WebLinksRequireHttpsGithubHost()
    {
        using var sandbox = new GitHubSandbox();
        using var handler = new ScriptedHandler(_ => JsonResponse("{}", HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var service = sandbox.CreateService(http, new TestClock());

        Assert.True(service.TryCreateWebUri("https://github.com/owner/repo/actions/runs/1", out var uri));
        Assert.Equal("github.com", uri!.Host);
        Assert.False(service.TryCreateWebUri("http://github.com/owner/repo", out _));
        Assert.False(service.TryCreateWebUri("https://evil.example/owner/repo", out _));
        Assert.False(service.TryCreateWebUri("https://user@github.com/owner/repo", out _));
    }

    private const string RepositoryJson =
        """{"full_name":"Owner/Repo","html_url":"https://github.com/Owner/Repo","private":true}""";

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status, string? etag = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (etag is not null)
        {
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    private sealed class GitHubSandbox(string? token = null) : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "devdesk-github-" + Guid.NewGuid().ToString("N"));
        public string CachePath => Path.Combine(_directory, "github-cache.json");
        public FakeCredentialStore Credentials { get; } = new(token);

        public GitHubSandbox() : this(null)
        {
        }

        public GitHubService CreateService(HttpClient http, TimeProvider time) =>
            new(new GitHubApiClient(http), Credentials, new GitHubCacheStore(CachePath), time);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeCredentialStore(string? token) : IGitHubCredentialStore
    {
        public string? Token { get; private set; } = token;
        public bool HasToken => !string.IsNullOrEmpty(Token);
        public string? ReadToken() => Token;
        public void SaveToken(string value) => Token = value;
        public void DeleteToken() => Token = null;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond =
            (request, _) => Task.FromResult(respond(request));

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            : this(_ => throw new InvalidOperationException())
        {
            _respond = respond;
        }

        public ConcurrentQueue<HttpRequestMessage> Captured { get; } = new();
        public System.Collections.Generic.IReadOnlyList<HttpRequestMessage> Requests => Captured.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Captured.Enqueue(request);
            return _respond(request, cancellationToken);
        }
    }
}
