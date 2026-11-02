using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

public sealed record GitHubApiResponse<T>(
    T? Data,
    string? Json,
    string? ETag,
    bool NotModified,
    GitHubErrorKind ErrorKind,
    string? Message,
    GitHubRateLimit? RateLimit)
{
    public bool Succeeded => ErrorKind == GitHubErrorKind.None && (Data is not null || NotModified);
}

public sealed class GitHubApiClient
{
    public const string ApiVersion = "2026-03-10";
    public const int MaximumCachedPayloadBytes = 2 * 1024 * 1024;
    private static readonly Uri ApiRoot = new("https://api.github.com/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public GitHubApiClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? CreateHttpClient();
    }

    public Task<GitHubApiResponse<GitHubUser>> GetAuthenticatedUserAsync(
        string token,
        CancellationToken cancellationToken = default) =>
        GetAsync<GitHubUser>("user", token, null, cancellationToken);

    public Task<GitHubApiResponse<GitHubRepository>> GetRepositoryAsync(
        GitHubRepositoryIdentity identity,
        string? token,
        string? etag = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<GitHubRepository>(RepositoryPath(identity), token, etag, cancellationToken);

    public Task<GitHubApiResponse<GitHubRelease[]>> GetReleasesAsync(
        GitHubRepositoryIdentity identity,
        string? token,
        string? etag = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<GitHubRelease[]>($"{RepositoryPath(identity)}/releases?per_page=20", token, etag, cancellationToken);

    public Task<GitHubApiResponse<GitHubWorkflowRuns>> GetWorkflowRunsAsync(
        GitHubRepositoryIdentity identity,
        string? token,
        string? etag = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<GitHubWorkflowRuns>($"{RepositoryPath(identity)}/actions/runs?per_page=20", token, etag, cancellationToken);

    public Task<GitHubApiResponse<GitHubPullRequest[]>> GetOpenPullRequestsAsync(
        GitHubRepositoryIdentity identity,
        string? token,
        string? etag = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<GitHubPullRequest[]>($"{RepositoryPath(identity)}/pulls?state=open&per_page=20", token, etag, cancellationToken);

    public Task<GitHubApiResponse<GitHubCheckRuns>> GetCheckRunsAsync(
        GitHubRepositoryIdentity identity,
        string commitSha,
        string? token,
        string? etag = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commitSha) || commitSha.Length > 128)
        {
            throw new ArgumentException("A commit SHA is required.", nameof(commitSha));
        }

        if (commitSha.Length is not (40 or 64) || !commitSha.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A commit SHA must be a 40- or 64-character hexadecimal value.", nameof(commitSha));
        }

        return GetAsync<GitHubCheckRuns>(
            $"{RepositoryPath(identity)}/commits/{Uri.EscapeDataString(commitSha)}/check-runs?per_page=50",
            token,
            etag,
            cancellationToken);
    }

    private async Task<GitHubApiResponse<T>> GetAsync<T>(
        string relativePath,
        string? token,
        string? etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoot, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            "DevDesk",
            AppInfo.Current.DisplayVersion.Replace(' ', '-')));

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var entityTag))
        {
            request.Headers.IfNoneMatch.Add(entityTag);
        }

        try
        {
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            var rateLimit = ReadRateLimit(response);
            var responseEtag = response.Headers.ETag?.ToString();

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new GitHubApiResponse<T>(
                    default, null, responseEtag ?? etag, true, GitHubErrorKind.None, null, rateLimit);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ErrorResponse<T>(response, rateLimit);
            }

            var json = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            try
            {
                var data = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (data is null)
                {
                    return new GitHubApiResponse<T>(
                        default, null, responseEtag, false, GitHubErrorKind.InvalidResponse,
                        "GitHub returned an empty or invalid response.", rateLimit);
                }

                return new GitHubApiResponse<T>(
                    data, json, responseEtag, false, GitHubErrorKind.None, null, rateLimit);
            }
            catch (JsonException)
            {
                return new GitHubApiResponse<T>(
                    default, null, responseEtag, false, GitHubErrorKind.InvalidResponse,
                    "GitHub returned a response DevDesk could not read.", rateLimit);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new GitHubApiResponse<T>(
                default, null, null, false, GitHubErrorKind.TimedOut,
                "The GitHub request timed out.", null);
        }
        catch (InvalidDataException)
        {
            return new GitHubApiResponse<T>(
                default, null, null, false, GitHubErrorKind.InvalidResponse,
                "GitHub returned a response larger than DevDesk can safely process.", null);
        }
        catch (HttpRequestException)
        {
            return new GitHubApiResponse<T>(
                default, null, null, false, GitHubErrorKind.Network,
                "GitHub could not be reached. Check the network connection and try again.", null);
        }
        catch (IOException)
        {
            return new GitHubApiResponse<T>(
                default, null, null, false, GitHubErrorKind.Network,
                "The GitHub response could not be read.", null);
        }
    }

    private static GitHubApiResponse<T> ErrorResponse<T>(
        HttpResponseMessage response,
        GitHubRateLimit? rateLimit)
    {
        var (kind, message) = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => (GitHubErrorKind.Authentication, "GitHub did not accept the saved token."),
            HttpStatusCode.Forbidden when rateLimit?.Remaining == 0 || rateLimit?.RetryAfter is not null =>
                (GitHubErrorKind.RateLimited, "GitHub temporarily limited requests. Wait before refreshing."),
            HttpStatusCode.Forbidden => (GitHubErrorKind.Forbidden, "GitHub denied access to this resource."),
            HttpStatusCode.NotFound => (GitHubErrorKind.NotFound, "This repository or resource was not found or is not accessible."),
            (HttpStatusCode)429 => (GitHubErrorKind.RateLimited, "GitHub temporarily limited requests. Wait before refreshing."),
            >= HttpStatusCode.InternalServerError => (GitHubErrorKind.Server, "GitHub is temporarily unavailable."),
            _ => (GitHubErrorKind.InvalidResponse, "GitHub returned an unexpected response.")
        };

        return new GitHubApiResponse<T>(default, null, null, false, kind, message, rateLimit);
    }

    private static GitHubRateLimit? ReadRateLimit(HttpResponseMessage response)
    {
        int? limit = ReadInt(response, "X-RateLimit-Limit");
        int? remaining = ReadInt(response, "X-RateLimit-Remaining");
        DateTimeOffset? resetsAt = null;
        var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            ? System.Linq.Enumerable.FirstOrDefault(resetValues)
            : null;
        if (long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetsAt = null;
            }
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate)
        {
            retryAfter = retryDate - DateTimeOffset.UtcNow;
        }

        if (retryAfter < TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
        }

        return limit is null && remaining is null && resetsAt is null && retryAfter is null
            ? null
            : new GitHubRateLimit(limit, remaining, resetsAt, retryAfter);
    }

    private static int? ReadInt(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)
            && int.TryParse(System.Linq.Enumerable.FirstOrDefault(values), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumCachedPayloadBytes)
        {
            throw new InvalidDataException("GitHub response exceeded the size limit.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumCachedPayloadBytes)
            {
                throw new InvalidDataException("GitHub response exceeded the size limit.");
            }

            output.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static string RepositoryPath(GitHubRepositoryIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.SupportsPublicApi)
        {
            throw new ArgumentException("Only github.com repositories can use the public GitHub API.", nameof(identity));
        }

        if (!IsPathSegment(identity.Owner) || !IsPathSegment(identity.Repository))
        {
            throw new ArgumentException("Repository identity contains an invalid owner or repository name.", nameof(identity));
        }

        return $"repos/{Uri.EscapeDataString(identity.Owner)}/{Uri.EscapeDataString(identity.Repository)}";
    }

    private static bool IsPathSegment(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 100
        && value is not "." and not ".."
        && value.IndexOfAny(['/', '\\', '?', '#']) < 0
        && !value.Contains("://", StringComparison.Ordinal)
        && !System.Linq.Enumerable.Any(value, char.IsControl);

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
