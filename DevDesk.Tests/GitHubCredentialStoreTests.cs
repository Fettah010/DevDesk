using System;
using System.IO;
using System.Security.Cryptography;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class GitHubCredentialStoreTests
{
    [Fact]
    public void ReadMigratesFallbackTokenToPrimaryWhenAvailable()
    {
        var primary = new FakeProtectedStore();
        var fallback = new FakeProtectedStore { Token = "fallback-token" };
        var store = new GitHubCredentialStore(primary, fallback);

        var token = store.ReadToken();

        Assert.Equal("fallback-token", token);
        Assert.Equal("fallback-token", primary.Token);
        Assert.Null(fallback.Token);
    }

    [Fact]
    public void SaveUsesProtectedFallbackWhenPrimaryIsUnavailable()
    {
        var primary = new FakeProtectedStore { FailWrites = true };
        var fallback = new FakeProtectedStore();
        var store = new GitHubCredentialStore(primary, fallback);

        store.SaveToken("new-token");

        Assert.Null(primary.Token);
        Assert.Equal("new-token", fallback.Token);
        Assert.Equal("new-token", store.ReadToken());
    }

    [Fact]
    public void SaveFailsClosedWhenNeitherProtectedStoreIsAvailable()
    {
        var primary = new FakeProtectedStore { FailWrites = true };
        var fallback = new FakeProtectedStore { FailWrites = true };
        var store = new GitHubCredentialStore(primary, fallback);

        Assert.Throws<IOException>(() => store.SaveToken("synthetic-token"));

        Assert.Null(primary.Token);
        Assert.Null(fallback.Token);
    }

    [Fact]
    public void DeleteRemovesBothCredentialCopies()
    {
        var primary = new FakeProtectedStore { Token = "primary-token" };
        var fallback = new FakeProtectedStore { Token = "fallback-token" };
        var store = new GitHubCredentialStore(primary, fallback);

        store.DeleteToken();

        Assert.Null(primary.Token);
        Assert.Null(fallback.Token);
        Assert.False(store.HasToken);
    }

    [Fact]
    public void DpapiFallbackEncryptsAndDeletesTokenFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "devdesk-dpapi-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "github-token.dpapi");
        const string token = "github_pat_synthetic-token-for-test";
        try
        {
            var store = new DpapiGitHubCredentialStore(path);
            store.SaveToken(token);

            var fileContents = File.ReadAllBytes(path);
            Assert.DoesNotContain(token, System.Text.Encoding.UTF8.GetString(fileContents), StringComparison.Ordinal);
            Assert.Equal(token, store.ReadToken());

            store.DeleteToken();

            Assert.False(File.Exists(path));
            Assert.Null(store.ReadToken());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task InvalidTokenCharactersAreRejectedBeforeAnyHttpRequest()
    {
        var handler = new NoRequestHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var directory = Path.Combine(Path.GetTempPath(), "devdesk-token-validation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new GitHubService(
                new GitHubApiClient(http),
                new FakeCredentialStore(),
                new GitHubCacheStore(Path.Combine(directory, "github-cache.json")));

            var result = await service.SaveAndValidateTokenAsync("token\r\nInjected: value");

            Assert.Equal(GitHubErrorKind.Authentication, result.ErrorKind);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FakeProtectedStore : IProtectedGitHubTokenStore
    {
        public string? Token { get; set; }
        public bool FailWrites { get; init; }

        public string? ReadToken() => Token;

        public void SaveToken(string token)
        {
            if (FailWrites)
            {
                throw new CryptographicException("synthetic storage failure");
            }

            Token = token;
        }

        public void DeleteToken() => Token = null;
    }

    private sealed class FakeCredentialStore : IGitHubCredentialStore
    {
        public bool HasToken => false;
        public string? ReadToken() => null;
        public void SaveToken(string token) => throw new NotSupportedException();
        public void DeleteToken() => throw new NotSupportedException();
    }

    private sealed class NoRequestHandler : System.Net.Http.HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("No request should be sent.");
        }
    }
}
