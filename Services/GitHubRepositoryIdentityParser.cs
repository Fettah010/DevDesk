using System;
using System.Text.RegularExpressions;
using DevDesk.Models;

namespace DevDesk.Services;

public static partial class GitHubRepositoryIdentityParser
{
    [GeneratedRegex(@"^(?:[^@/:]+@)?(?<host>[^/:]+):(?<path>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpRemotePattern();

    public static GitHubRepositoryIdentity? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var value = remoteUrl.Trim();
        string host;
        string path;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase)))
        {
            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    return null;
                }
            }
            else if (uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.Port is not (-1 or 22) || uri.UserInfo.Contains(':'))
                {
                    return null;
                }
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                return null;
            }

            host = uri.IdnHost;
            path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        }
        else
        {
            if (value.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }

            var match = ScpRemotePattern().Match(value);
            if (!match.Success)
            {
                return null;
            }

            host = match.Groups["host"].Value;
            path = match.Groups["path"].Value;
        }

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return null;
        }

        var segments = path.Trim('/').Split('/');
        if (segments.Length != 2)
        {
            return null;
        }

        var owner = DecodeSegment(segments[0]);
        var repository = DecodeSegment(segments[1]);
        if (owner is null || repository is null)
        {
            return null;
        }

        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repository = repository[..^4];
        }

        if (repository.Length == 0 || repository is "." or "..")
        {
            return null;
        }

        return new GitHubRepositoryIdentity(host.ToLowerInvariant(), owner, repository);
    }

    private static string? DecodeSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(segment);
        if (decoded.Length == 0
            || decoded is "." or ".."
            || decoded.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            return null;
        }

        foreach (var character in decoded)
        {
            if (char.IsControl(character))
            {
                return null;
            }
        }

        return decoded;
    }
}
