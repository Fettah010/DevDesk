using System;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// Reads MSBuild diagnostics by shape and code, never by prose: a localized SDK
/// writes "Fehler" or "erreur", but the <c>file(line,col): error CS1002:</c>
/// frame and the code are stable (roadmap risk table).
/// <para>
/// Two shapes are recognised: the full one with a file and position, and the bare
/// one a task emits without a file (<c>CSC : error CS2001: …</c>). Anything else
/// is output, not a problem — inventing a diagnostic from a warning line would
/// fill the list with noise the user cannot click.
/// </para>
/// </summary>
public static class MsBuildProblemParser
{
    public static BuildProblem? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return ParseWithLocation(line) ?? ParseBare(line);
    }

    private static BuildProblem? ParseWithLocation(string line)
    {
        // C:\path\File.cs(12,5): error CS1002: ; expected [C:\path\App.csproj]
        var open = line.IndexOf('(');
        if (open <= 0)
        {
            return null;
        }

        var close = line.IndexOf(')', open + 1);
        if (close < 0)
        {
            return null;
        }

        var location = line.Substring(open + 1, close - open - 1);
        var parts = location.Split(',');
        if (parts.Length is < 1 or > 3 || !int.TryParse(parts[0].Trim(), out var lineNumber))
        {
            return null;
        }

        int? column = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var columnNumber)
            ? columnNumber
            : null;

        var rest = line.Substring(close + 1).TrimStart();
        if (rest.Length == 0 || rest[0] != ':')
        {
            return null;
        }

        var diagnostic = ParseDiagnostic(rest.Substring(1).Trim());
        if (diagnostic is null)
        {
            return null;
        }

        return diagnostic with
        {
            File = line.Substring(0, open).Trim(),
            Line = lineNumber,
            Column = column,
        };
    }

    private static BuildProblem? ParseBare(string line)
    {
        // "error CS1002: ; expected [C:\path\App.csproj]"
        // "CSC : error CS2001: Source file 'x' could not be found."
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var prefix = line.Substring(0, colon).Trim();
        var rest = line.Substring(colon + 1).Trim();

        // "error CS1002: …" — the severity is the first word of the line.
        if (IsSeverity(prefix) || StartsWithWord(prefix, "error") || StartsWithWord(prefix, "warning"))
        {
            return ParseDiagnostic(line);
        }

        // The part before ':' is a tool name; the severity must follow it.
        var second = rest.IndexOf(':');
        if (second < 0)
        {
            return null;
        }

        var afterTool = rest.Substring(0, second).Trim();
        var firstWord = afterTool.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (firstWord.Length == 0 || !IsSeverity(firstWord[0]))
        {
            return null;
        }

        // Parse from the severity word: the text after the tool's colon is the
        // same "error CODE: message" shape, with the tool kept as context.
        return ParseDiagnostic(rest, prefix);
    }

    private static BuildProblem? ParseDiagnostic(string text, string? tool = null)
    {
        var severity = ReadSeverity(text, out var afterSeverity);
        if (severity is null)
        {
            return null;
        }

        string? code = null;
        var body = afterSeverity;

        var codeEnd = body.IndexOf(':');
        if (codeEnd > 0)
        {
            var candidate = body.Substring(0, codeEnd).Trim();
            if (LooksLikeCode(candidate))
            {
                code = candidate;
                body = body.Substring(codeEnd + 1).Trim();
            }
        }

        var message = body.Trim();
        string? project = null;

        // MSBuild appends the project in brackets; it is context, not message.
        var bracket = message.LastIndexOf(" [", StringComparison.Ordinal);
        if (bracket > 0 && message.EndsWith(']'))
        {
            project = message.Substring(bracket + 2, message.Length - bracket - 3);
            message = message.Substring(0, bracket).Trim();
        }

        return new BuildProblem(
            severity.Value,
            code,
            File: string.Empty,
            Line: null,
            Column: null,
            Message: message,
            Project: project ?? tool);
    }

    private static BuildProblemSeverity? ReadSeverity(string text, out string after)
    {
        after = text;

        if (StartsWithWord(text, "error"))
        {
            after = text.Substring("error".Length).TrimStart(' ', ':').TrimStart();
            return BuildProblemSeverity.Error;
        }

        if (StartsWithWord(text, "warning"))
        {
            after = text.Substring("warning".Length).TrimStart(' ', ':').TrimStart();
            return BuildProblemSeverity.Warning;
        }

        return null;
    }

    /// <summary>"error" but not "errorReport": a word boundary after the severity.</summary>
    private static bool StartsWithWord(string text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (text.Length == word.Length || !char.IsLetter(text[word.Length]));

    private static bool IsSeverity(string text) =>
        string.Equals(text, "error", StringComparison.OrdinalIgnoreCase)
        || string.Equals(text, "warning", StringComparison.OrdinalIgnoreCase);

    /// <summary>CS1002, MSB1009, NU1900, CA1416 — letters then digits, nothing else.</summary>
    private static bool LooksLikeCode(string text)
    {
        if (text.Length is < 3 or > 12)
        {
            return false;
        }

        var letters = 0;
        while (letters < text.Length && char.IsLetter(text[letters]))
        {
            letters++;
        }

        if (letters is < 1 or > 5 || letters == text.Length)
        {
            return false;
        }

        for (var index = letters; index < text.Length; index++)
        {
            if (!char.IsDigit(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
