using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The runner is the only thing in the app that starts a compiler, so its
/// contract is checked against real processes rather than a mock: streaming,
/// exit codes, cancellation, timeout, tree kill, environment and the argument
/// list that keeps <c>DevTem WinUI 3</c> one argument.
/// </summary>
public class ProcessRunnerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RunAsync_ReportsLinesWhileTheProcessIsStillRunning()
    {
        using var script = TempScript.Create(
            "@echo off",
            "echo first-line",
            "ping -n 4 127.0.0.1 >nul",
            "echo second-line");

        var lines = new RecordingProgress();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lines.LineReported += (_, line) =>
        {
            if (line.Text.Contains("first-line", StringComparison.Ordinal))
            {
                first.TrySetResult(line.Text);
            }
        };

        var run = new ProcessRunner().RunAsync(
            ProcessRequest.For("cmd.exe", TempDirectory(), "/c", script.Path),
            lines,
            CancellationToken.None);

        // The first line must arrive before the process exits: that is what makes
        // the console live instead of a result shown after the fact.
        var completed = await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(first.Task, completed);
        Assert.False(run.IsCompleted, "output was only reported after the process had finished");

        var result = await run;

        Assert.Equal(ProcessOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines.All, line => line.Text.Contains("second-line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ReportsANonZeroExitCodeAsFailed()
    {
        var result = await new ProcessRunner().RunAsync(
            ProcessRequest.For("cmd.exe", TempDirectory(), "/c", "exit 3"),
            output: null,
            CancellationToken.None);

        Assert.Equal(ProcessOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("3", result.FailureHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CancelKillsTheProcessAndIsNotAFailure()
    {
        using var script = TempScript.Create(
            "@echo off",
            "echo started",
            "ping -n 60 127.0.0.1 >nul");

        using var cancellation = new CancellationTokenSource();
        var lines = new RecordingProgress();
        lines.LineReported += (_, line) =>
        {
            if (line.Text.Contains("started", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };

        var result = await new ProcessRunner().RunAsync(
            ProcessRequest.For("cmd.exe", TempDirectory(), "/c", script.Path),
            lines,
            cancellation.Token);

        Assert.Equal(ProcessOutcome.Canceled, result.Outcome);
        Assert.True(result.Duration < Short, $"cancel took {result.Duration}");
    }

    [Fact]
    public async Task RunAsync_TimeoutStopsTheProcessAndSaysSo()
    {
        using var script = TempScript.Create(
            "@echo off",
            "ping -n 60 127.0.0.1 >nul");

        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest("cmd.exe", new[] { "/c", script.Path }, TempDirectory(), Timeout: TimeSpan.FromSeconds(1)),
            output: null,
            CancellationToken.None);

        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.NotNull(result.FailureHint);
        Assert.Contains("Stopped after", result.FailureHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingExecutableIsNotFoundNotAnException()
    {
        var result = await new ProcessRunner().RunAsync(
            ProcessRequest.For("devdesk-no-such-tool-4c1f.exe", TempDirectory()),
            output: null,
            CancellationToken.None);

        Assert.Equal(ProcessOutcome.NotFound, result.Outcome);
        Assert.Contains("was not found", result.FailureHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_MissingWorkingDirectoryIsNotFound()
    {
        var missing = Path.Combine(TempDirectory(), "no-such-folder-" + Guid.NewGuid().ToString("N"));

        var result = await new ProcessRunner().RunAsync(
            ProcessRequest.For("cmd.exe", missing, "/c", "exit 0"),
            output: null,
            CancellationToken.None);

        Assert.Equal(ProcessOutcome.NotFound, result.Outcome);
        Assert.Contains("does not exist", result.FailureHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_InjectsTheRequestedEnvironment()
    {
        using var script = TempScript.Create(
            "@echo off",
            "echo value=%DEVDESK_TEST_VALUE%");

        var lines = new RecordingProgress();
        var environment = new Dictionary<string, string> { ["DEVDESK_TEST_VALUE"] = "hello-42" };

        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest("cmd.exe", new[] { "/c", script.Path }, TempDirectory(), environment),
            lines,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(lines.All, line => line.Text.Contains("value=hello-42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_KeepsAPathWithSpacesAsOneArgument()
    {
        var spaced = Path.Combine(TempDirectory(), "folder with spaces");
        Directory.CreateDirectory(spaced);

        using var script = TempScript.Create(
            "@echo off",
            "echo arg=%~1");

        var lines = new RecordingProgress();

        var result = await new ProcessRunner().RunAsync(
            ProcessRequest.For("cmd.exe", TempDirectory(), "/c", script.Path, spaced),
            lines,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(lines.All, line => line.Text.Contains($"arg={spaced}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_CancelLeavesNoChildProcessBehind()
    {
        using var script = TempScript.Create(
            "@echo off",
            "echo started",
            "ping -n 30 127.0.0.1 >nul");

        var before = Pinging();
        using var cancellation = new CancellationTokenSource();
        var lines = new RecordingProgress();
        lines.LineReported += (_, line) =>
        {
            if (line.Text.Contains("started", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };

        await new ProcessRunner().RunAsync(
            ProcessRequest.For("cmd.exe", TempDirectory(), "/c", script.Path),
            lines,
            cancellation.Token);

        // The tree kill is the promise: a cancelled build must not leave a
        // compiler (or here, its stand-in) running behind the window.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        int[] leaked;
        do
        {
            leaked = Pinging().Except(before).ToArray();
            if (leaked.Length == 0)
            {
                break;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Empty(leaked);
    }

    private static int[] Pinging() =>
        Process.GetProcessesByName("ping").Select(process => process.Id).ToArray();

    private static string TempDirectory() => Path.GetTempPath();

    private sealed class RecordingProgress : IProgress<ProcessOutputLine>
    {
        private readonly ConcurrentQueue<ProcessOutputLine> _lines = new();

        public event EventHandler<ProcessOutputLine>? LineReported;

        public IReadOnlyList<ProcessOutputLine> All => _lines.ToArray();

        public void Report(ProcessOutputLine value)
        {
            _lines.Enqueue(value);
            LineReported?.Invoke(this, value);
        }
    }

    private sealed class TempScript : IDisposable
    {
        private TempScript(string path) => Path = path;

        public string Path { get; }

        public static TempScript Create(params string[] lines)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "devdesk-script-" + Guid.NewGuid().ToString("N") + ".cmd");

            File.WriteAllLines(path, lines);
            return new TempScript(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
