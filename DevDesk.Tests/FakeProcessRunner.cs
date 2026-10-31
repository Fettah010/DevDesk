using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Services;

namespace DevDesk.Tests;

/// <summary>
/// A process runner a test scripts instead of a real compiler: it records what
/// would have been executed, streams the lines the test names, and can block,
/// fail, or be cancelled. No test in this suite ever starts <c>dotnet build</c>
/// against a real repository.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = new();

    /// <summary>Lines reported for the next call, before the result is returned.</summary>
    public List<ProcessOutputLine> Lines { get; } = new();

    /// <summary>Optional hook that runs before the scripted result; lets a test block or cancel.</summary>
    public Func<ProcessRequest, CancellationToken, Task>? Before { get; set; }

    public ProcessOutcome Outcome { get; set; } = ProcessOutcome.Succeeded;

    public int ExitCode { get; set; }

    public string? FailureHint { get; set; }

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        foreach (var line in Lines)
        {
            output?.Report(line);
        }

        if (Before is not null)
        {
            await Before(request, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new ProcessResult(Outcome, ExitCode, string.Empty, TimeSpan.Zero, FailureHint);
    }
}
