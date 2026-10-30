using System.Text.RegularExpressions;

namespace DevDesk.Services;

/// <summary>
/// Removes credentials from a string before it is written anywhere a human or a
/// zip can see it (roadmap §6.5.2, §6.5.1).
/// <para>
/// Applied at the ring-buffer sink, which is the point every diagnostics
/// surface reads from: the file sink, the page, copy-visible and the export
/// bundle all inherit the redacted text, so there is no path where a raw token
/// escapes because someone forgot to call this.
/// </para>
/// <para>
/// The patterns are deliberately narrow and testable with literal strings
/// rather than clever: an over-broad regex that eats ordinary log text is its
/// own bug.
/// </para>
/// </summary>
public static class LogRedaction
{
    public const string Placeholder = "***";

    // scheme://user:pass@host — the userinfo part is the secret.
    private static readonly Regex UserInfo = new(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)(?<userinfo>[^/@\s]+)@",
        RegexOptions.Compiled);

    // ?token=…, &access_token=…, &sig=… — the name is kept, the value is not.
    private static readonly Regex QueryValue = new(
        @"[?&](?<name>token|access_token|api_key|apikey|key|secret|signature|sig|password|auth|code)=(?<value>[^&\s""'#]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // GitHub tokens keep their kind prefix so support knows what leaked.
    private static readonly Regex TokenPrefixes = new(
        @"\b(?<kind>gh[pousr]|github_pat)_[A-Za-z0-9_]{16,}",
        RegexOptions.Compiled);

    // Authorization: Bearer …, Authorization: token …
    private static readonly Regex Bearer = new(
        @"(?<header>bearer\s+|token\s+)(?<value>[A-Za-z0-9_\-\.]{12,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns <paramref name="text"/> with credentials replaced by <see cref="Placeholder"/>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = UserInfo.Replace(text, match =>
            match.Groups["userinfo"].Value.Contains(':')
                ? $"{match.Groups["scheme"].Value}{Placeholder}:{Placeholder}@"
                : $"{match.Groups["scheme"].Value}{Placeholder}@");

        redacted = QueryValue.Replace(redacted, match =>
            $"{match.Value[0]}{match.Groups["name"].Value}={Placeholder}");

        redacted = TokenPrefixes.Replace(redacted, match => $"{match.Groups["kind"].Value}_{Placeholder}");

        return Bearer.Replace(redacted, match => $"{match.Groups["header"].Value}{Placeholder}");
    }
}
