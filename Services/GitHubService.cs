using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

public sealed class GitHubService : IReleaseWorkflowRunReader
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GitHubApiClient _api;
    private readonly IGitHubCredentialStore _credentials;
    private readonly GitHubCacheStore _cache;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestLocks = new(StringComparer.Ordinal);

    public GitHubService(
        GitHubApiClient api,
        IGitHubCredentialStore credentials,
        GitHubCacheStore cache,
        TimeProvider? time = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _time = time ?? TimeProvider.System;
    }

    public bool HasToken => _credentials.HasToken;

    public GitHubDataResult<GitHubRepository>? GetCachedRepository(GitHubRepositoryIdentity identity) =>
        GetCached<GitHubRepository>(identity, "repository");

    public GitHubDataResult<GitHubRelease[]>? GetCachedReleases(GitHubRepositoryIdentity identity) =>
        GetCached<GitHubRelease[]>(identity, "releases:per_page=20");

    public GitHubDataResult<GitHubWorkflowRuns>? GetCachedWorkflowRuns(GitHubRepositoryIdentity identity) =>
        GetCached<GitHubWorkflowRuns>(identity, "actions:runs:per_page=20");

    public GitHubDataResult<GitHubPullRequest[]>? GetCachedOpenPullRequests(GitHubRepositoryIdentity identity) =>
        GetCached<GitHubPullRequest[]>(identity, "pulls:open:per_page=20");

    public async Task<GitHubDataResult<GitHubUser>> SaveAndValidateTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Failure<GitHubUser>(GitHubErrorKind.Authentication, "Enter a personal access token.");
        }

        if (token.Length > 4096 || token.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            return Failure<GitHubUser>(GitHubErrorKind.Authentication, "The token contains unsupported characters.");
        }

        var validation = await _api.GetAuthenticatedUserAsync(token, cancellationToken).ConfigureAwait(false);
        if (!validation.Succeeded || validation.Data is null)
        {
            return Failure<GitHubUser>(
                validation.ErrorKind,
                validation.Message ?? "GitHub could not validate the token.",
                validation.RateLimit);
        }

        try
        {
            _cache.Clear();
            _credentials.SaveToken(token);
            return Success(validation.Data, false, false, _time.GetUtcNow(), validation.RateLimit);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or System.Runtime.InteropServices.COMException)
        {
            LoggingService.Log.Warning("GitHub token was validated but could not be stored securely");
            return Failure<GitHubUser>(
                GitHubErrorKind.Server,
                "The token is valid, but secure credential storage is unavailable. It was not saved.");
        }
    }

    public GitHubDataResult<bool> GetCredentialStatus() =>
        new(HasToken, GitHubErrorKind.None, null, false, false, null, null);

    public async Task<GitHubDataResult<GitHubUser>> GetCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var token = _credentials.ReadToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return Failure<GitHubUser>(GitHubErrorKind.NoCredential, "Not signed in. Public repository data remains available.");
            }

            var response = await _api.GetAuthenticatedUserAsync(token, cancellationToken).ConfigureAwait(false);
            return response.Succeeded && response.Data is not null
                ? Success(response.Data, false, false, _time.GetUtcNow(), response.RateLimit)
                : Failure<GitHubUser>(
                    response.ErrorKind,
                    response.Message ?? "GitHub account could not be read.",
                    response.RateLimit);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return Failure<GitHubUser>(GitHubErrorKind.Server, "The saved credential could not be read securely.");
        }
    }

    public Task<GitHubDataResult<GitHubRepository>> GetRepositoryAsync(
        GitHubRepositoryIdentity identity,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        FetchAsync(
            identity,
            "repository",
            forceRefresh,
            (token, etag, ct) => _api.GetRepositoryAsync(identity, token, etag, ct),
            cancellationToken);

    public Task<GitHubDataResult<GitHubRelease[]>> GetReleasesAsync(
        GitHubRepositoryIdentity identity,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        FetchAsync(
            identity,
            "releases:per_page=20",
            forceRefresh,
            (token, etag, ct) => _api.GetReleasesAsync(identity, token, etag, ct),
            cancellationToken);

    public Task<GitHubDataResult<GitHubWorkflowRuns>> GetWorkflowRunsAsync(
        GitHubRepositoryIdentity identity,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        FetchAsync(
            identity,
            "actions:runs:per_page=20",
            forceRefresh,
            (token, etag, ct) => _api.GetWorkflowRunsAsync(identity, token, etag, ct),
            cancellationToken);

    public Task<GitHubDataResult<GitHubPullRequest[]>> GetOpenPullRequestsAsync(
        GitHubRepositoryIdentity identity,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        FetchAsync(
            identity,
            "pulls:open:per_page=20",
            forceRefresh,
            (token, etag, ct) => _api.GetOpenPullRequestsAsync(identity, token, etag, ct),
            cancellationToken);

    public async Task<GitHubDataResult<GitHubPullRequestSummary[]>> GetOpenPullRequestSummariesAsync(
        GitHubRepositoryIdentity identity,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var pulls = await GetOpenPullRequestsAsync(identity, forceRefresh, cancellationToken).ConfigureAwait(false);
        if (!pulls.Succeeded || pulls.Data is null)
        {
            return new GitHubDataResult<GitHubPullRequestSummary[]>(
                null, pulls.ErrorKind, pulls.Message, pulls.IsFromCache, pulls.IsStale, pulls.FetchedAt, pulls.RateLimit);
        }

        var summaries = new GitHubPullRequestSummary[Math.Min(pulls.Data.Length, 10)];
        for (var index = 0; index < summaries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pull = pulls.Data[index];
            var checksText = await GetChecksSummaryAsync(identity, pull, forceRefresh, cancellationToken)
                .ConfigureAwait(false);
            summaries[index] = new GitHubPullRequestSummary(pull, checksText);
        }

        return Success(
            summaries,
            pulls.IsFromCache,
            pulls.IsStale,
            pulls.FetchedAt ?? _time.GetUtcNow(),
            pulls.RateLimit);
    }

    public Task SignOutAsync()
    {
        Exception? failure = null;
        try
        {
            _credentials.DeleteToken();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            _cache.Clear();
        }
        catch (Exception ex)
        {
            failure = failure is null ? ex : new AggregateException(failure, ex);
        }

        if (failure is not null)
        {
            throw new IOException("GitHub sign-out could not remove all local credential data.", failure);
        }

        return Task.CompletedTask;
    }

    public bool TryCreateWebUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private async Task<GitHubDataResult<T>> FetchAsync<T>(
        GitHubRepositoryIdentity identity,
        string resource,
        bool forceRefresh,
        Func<string?, string?, CancellationToken, Task<GitHubApiResponse<T>>> fetch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.SupportsPublicApi)
        {
            return Failure<T>(
                GitHubErrorKind.NotFound,
                "GitHub API enrichment is supported only for github.com repositories.");
        }

        var key = CacheKey(identity, resource);
        var requestedAt = _time.GetUtcNow();
        var cached = _cache.Get(key);
        if (!forceRefresh && IsFresh(cached))
        {
            return DeserializeCached<T>(cached!, isStale: false, null);
        }

        var requestLock = _requestLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cache.Get(key);
            if ((!forceRefresh && IsFresh(cached))
                || (forceRefresh && cached is not null && cached.FetchedAt > requestedAt))
            {
                return DeserializeCached<T>(cached!, isStale: false, null);
            }

            var token = _credentials.ReadToken();
            var response = await fetch(token, cached?.ETag, cancellationToken).ConfigureAwait(false);
            if (response.NotModified)
            {
                if (cached is null)
                {
                    return Failure<T>(
                        GitHubErrorKind.InvalidResponse,
                        "GitHub returned an unchanged response without cached data.");
                }

                var refreshed = cached with
                {
                    ETag = response.ETag ?? cached.ETag,
                    FetchedAt = _time.GetUtcNow()
                };
                TryStore(key, refreshed);
                return DeserializeCached<T>(refreshed, isStale: false, response.RateLimit);
            }

            if (response.Succeeded && response.Data is not null && response.Json is not null)
            {
                TryStore(key, new GitHubCacheEntry(response.Json, response.ETag, _time.GetUtcNow()));
                return Success(response.Data, false, false, _time.GetUtcNow(), response.RateLimit);
            }

            if (cached is not null && CanUseStale(response.ErrorKind))
            {
                var stale = DeserializeCached<T>(cached, isStale: true, response.RateLimit);
                return stale with { Message = response.Message };
            }

            return Failure<T>(
                response.ErrorKind,
                response.Message ?? "GitHub request failed.",
                response.RateLimit);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Runtime.InteropServices.COMException)
        {
            LoggingService.Log.Warning("GitHub data could not be read from its local cache or credential store");
            return Failure<T>(
                GitHubErrorKind.Server,
                "GitHub data could not be loaded from local storage.");
        }
        finally
        {
            requestLock.Release();
        }
    }

    private bool IsFresh(GitHubCacheEntry? entry) =>
        entry is not null && _time.GetUtcNow() - entry.FetchedAt < CacheLifetime;

    private GitHubDataResult<T>? GetCached<T>(GitHubRepositoryIdentity identity, string resource)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.SupportsPublicApi)
        {
            return null;
        }

        var cached = _cache.Get(CacheKey(identity, resource));
        return cached is null
            ? null
            : DeserializeCached<T>(cached, isStale: !IsFresh(cached), null);
    }

    private static string CacheKey(GitHubRepositoryIdentity identity, string resource) =>
        $"{identity.Host}/{identity.Owner.ToLowerInvariant()}/{identity.Repository.ToLowerInvariant()}/{resource}";

    private void TryStore(string key, GitHubCacheEntry entry)
    {
        try
        {
            _cache.Set(key, entry);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LoggingService.Log.Warning("GitHub response could not be cached");
        }
    }

    private static bool CanUseStale(GitHubErrorKind error) =>
        error is GitHubErrorKind.Network or GitHubErrorKind.TimedOut or GitHubErrorKind.Server;

    private async Task<string> GetChecksSummaryAsync(
        GitHubRepositoryIdentity identity,
        GitHubPullRequest pull,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pull.Head?.Sha))
        {
            return "Checks unavailable";
        }

        var result = await FetchAsync(
            identity,
            $"checks:{pull.Head.Sha}:per_page=50",
            forceRefresh,
            (token, etag, ct) => _api.GetCheckRunsAsync(identity, pull.Head.Sha, token, etag, ct),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Data is null)
        {
            return result.IsStale ? "Checks cached" : "Checks unavailable";
        }

        var checks = result.Data.Runs;
        if (checks.Count == 0)
        {
            return "No checks";
        }

        if (checks.Any(check => !string.Equals(check.Status, "completed", StringComparison.OrdinalIgnoreCase)))
        {
            return "Checks running";
        }

        return checks.Any(check => check.Conclusion is "failure" or "timed_out" or "action_required")
            ? "Checks failing"
            : checks.All(check => check.Conclusion is "success" or "neutral" or "skipped")
                ? "Checks passing"
                : "Checks complete";
    }

    private static GitHubDataResult<T> DeserializeCached<T>(
        GitHubCacheEntry entry,
        bool isStale,
        GitHubRateLimit? rateLimit)
    {
        try
        {
            var data = JsonSerializer.Deserialize<T>(entry.Payload, JsonOptions);
            if (data is not null)
            {
                return Success(data, true, isStale, entry.FetchedAt, rateLimit);
            }
        }
        catch (JsonException)
        {
        }

        return Failure<T>(GitHubErrorKind.InvalidResponse, "Cached GitHub data could not be read.", rateLimit);
    }

    private static GitHubDataResult<T> Success<T>(
        T data,
        bool fromCache,
        bool stale,
        DateTimeOffset fetchedAt,
        GitHubRateLimit? rateLimit) =>
        new(data, GitHubErrorKind.None, null, fromCache, stale, fetchedAt, rateLimit);

    private static GitHubDataResult<T> Failure<T>(
        GitHubErrorKind kind,
        string message,
        GitHubRateLimit? rateLimit = null) =>
        new(default, kind, message, false, false, null, rateLimit);
}
