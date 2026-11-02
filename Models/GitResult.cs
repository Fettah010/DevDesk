using System;

namespace DevDesk.Models;

/// <summary>
/// The result of the optional `git init` + first commit that the template
/// wizard can run on the new app.
/// </summary>
public sealed record GitResult(
    bool Succeeded,
    string? TargetPath,
    string? FirstCommitHash,
    string? Error)
{
    public string SummaryText =>
        Succeeded
            ? $"git init · first commit {FirstCommitHash?[..Math.Min(7, FirstCommitHash.Length)] ?? "?"}"
            : $"git init failed · {Error ?? "unknown error"}";
}
