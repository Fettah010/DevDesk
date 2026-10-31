using System;
using System.Collections.Generic;
using System.IO;
using DevDesk.Services;

namespace DevDesk.Models;

public enum BuildProblemSeverity
{
    Error,
    Warning,
}

/// <summary>
/// One MSBuild diagnostic (W-0.3.5). The code and the location are the contract;
/// the message is whatever the tool said, in whatever language it said it.
/// </summary>
public sealed record BuildProblem(
    BuildProblemSeverity Severity,
    string? Code,
    string File,
    int? Line,
    int? Column,
    string Message,
    string? Project)
{
    public bool IsError => Severity == BuildProblemSeverity.Error;

    public string FileName => string.IsNullOrEmpty(File) ? string.Empty : Path.GetFileName(File);

    public bool HasFile => !string.IsNullOrEmpty(File);

    public bool HasLocation => HasFile && Line is not null;

    /// <summary>"Formixa.csproj:12:5" — what a problem row shows before the message.</summary>
    public string Location
    {
        get
        {
            if (!HasFile)
            {
                return string.IsNullOrEmpty(Code) ? "build" : Code!;
            }

            if (Line is not { } line)
            {
                return FileName;
            }

            return Column is { } column ? $"{FileName}:{line}:{column}" : $"{FileName}:{line}";
        }
    }

    public string CodeText => Code ?? string.Empty;

    public string SeverityLabel => IsError ? "Error" : "Warning";

    /// <summary>Segoe Fluent glyph for the row; the model already carries glyphs elsewhere.</summary>
    public string Glyph => IsError ? "\uE783" : "\uE7BA";

    /// <summary>"file(line,col): error CS1002: message" — the copyable form.</summary>
    public string ToDiagnosticText() =>
        HasLocation
            ? $"{File}({Line},{Column ?? 1}): {(IsError ? "error" : "warning")} {CodeText}: {Message}"
            : $"{(IsError ? "error" : "warning")} {CodeText}: {Message}".Trim();
}

/// <summary>
/// Feeds output lines to the parser and keeps the problems (and the first error)
/// as the build runs, so a failure can be reported before the log is even closed.
/// </summary>
public sealed class BuildProblemCollector
{
    public const int DefaultLimit = 500;

    private readonly List<BuildProblem> _problems = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly int _limit;
    private int _lastIndex = -1;

    public BuildProblemCollector(int limit = DefaultLimit) => _limit = Math.Max(1, limit);

    public IReadOnlyList<BuildProblem> Problems => _problems;

    public bool Truncated { get; private set; }

    public int ErrorCount
    {
        get
        {
            var count = 0;
            foreach (var problem in _problems)
            {
                if (problem.IsError)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public int WarningCount => _problems.Count - ErrorCount;

    public string? FirstError
    {
        get
        {
            foreach (var problem in _problems)
            {
                if (problem.IsError)
                {
                    return problem.ToDiagnosticText();
                }
            }

            return null;
        }
    }

    public void Add(string line)
    {
        var problem = MsBuildProblemParser.ParseLine(line);

        if (problem is null)
        {
            // A wrapped message continues with indentation; anything else ends the
            // previous problem and is ordinary output.
            if (_lastIndex >= 0 && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                var previous = _problems[_lastIndex];
                _problems[_lastIndex] = previous with { Message = $"{previous.Message} {line.Trim()}" };
            }
            else
            {
                _lastIndex = -1;
            }

            return;
        }

        var key = $"{problem.Severity}|{problem.Code}|{problem.File}|{problem.Line}|{problem.Message}";
        if (!_seen.Add(key))
        {
            return;
        }

        if (_problems.Count >= _limit)
        {
            Truncated = true;
            return;
        }

        _problems.Add(problem);
        _lastIndex = _problems.Count - 1;
    }
}
