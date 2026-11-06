using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevDesk.Services;

public sealed record IgnoreRuleDecision(
    string RelativePath,
    bool IsExcluded,
    bool IsSafetyExcluded,
    string? MatchingRule);

/// <summary>Applies ordered root-relative glob rules without relaxing path safety.</summary>
public sealed class IgnoreRuleMatcher
{
    private readonly IReadOnlyList<(bool Negated, string Glob, Regex Pattern)> _rules;

    public IgnoreRuleMatcher(IEnumerable<string>? patterns)
    {
        var rules = new List<(bool, string, Regex)>
        {
            (false, ".*", Compile(".*")),
        };

        foreach (var value in patterns ?? PathHygiene.IgnoredFolderNames)
        {
            if (TryParse(value, out var negated, out var pattern))
            {
                rules.Add((negated, pattern, Compile(pattern)));
            }
        }

        _rules = rules;
    }

    public bool IsExcluded(string relativePath)
        => Evaluate(relativePath).IsExcluded;

    public IgnoreRuleDecision Evaluate(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Length == 0)
        {
            return new IgnoreRuleDecision(normalized, false, false, null);
        }

        var safetySegment = normalized.Split('/').FirstOrDefault(PathHygiene.IsSafetyIgnoredFolderName);
        if (safetySegment is not null)
        {
            return new IgnoreRuleDecision(normalized, true, true, safetySegment);
        }

        var excluded = false;
        string? matchedRule = null;
        foreach (var (negated, glob, pattern) in _rules)
        {
            if (pattern.IsMatch(normalized))
            {
                excluded = !negated;
                matchedRule = (negated ? "!" : string.Empty) + glob;
            }
        }

        return new IgnoreRuleDecision(normalized, excluded, false, matchedRule);
    }

    public IReadOnlyList<IgnoreRuleDecision> Preview(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        return relativePaths.Select(Evaluate).ToArray();
    }

    public bool MayContainNegatedPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        return _rules.Any(rule =>
            rule.Negated
            && (rule.Glob.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)
                || rule.Glob.StartsWith("**/", StringComparison.Ordinal)));
    }

    public static bool IsValidPattern(string? value)
    {
        if (!TryParse(value, out _, out var pattern))
        {
            return false;
        }

        try
        {
            _ = Compile(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParse(string? value, out bool negated, out string pattern)
    {
        negated = false;
        pattern = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().Replace('\\', '/');
        if (candidate.StartsWith('!'))
        {
            negated = true;
            candidate = candidate[1..];
        }

        candidate = candidate.Trim('/');
        if (candidate.Length == 0
            || candidate.Contains(':')
            || candidate.Split('/').Any(segment => segment is "." or ".."))
        {
            return false;
        }

        pattern = candidate;
        return true;
    }

    private static Regex Compile(string pattern)
    {
        var expression = new System.Text.StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    expression.Append(".*");
                    index++;
                }
                else
                {
                    expression.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                expression.Append("[^/]");
            }
            else if (character == '/')
            {
                expression.Append('/');
            }
            else
            {
                expression.Append(Regex.Escape(character.ToString()));
            }
        }

        var source = pattern.Contains('/')
            ? $"^{expression}(?:/.*)?$"
            : $"(?:^|.*/){expression}(?:/.*)?$";
        return new Regex(source, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static string NormalizeRelativePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim('/');
}
