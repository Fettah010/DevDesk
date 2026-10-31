using System;
using System.IO;
using System.Text;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// The name of a build log. One file per run, in the app's own data directory,
/// named so a human can find the run without opening anything: the time, the
/// verb and the project (D-0.3.5).
/// </summary>
public static class BuildLogFile
{
    public const int MaxProjectNameLength = 60;

    public static string Create(string directory, OperationKind kind, string targetName, DateTimeOffset at)
    {
        var project = Sanitize(Path.GetFileNameWithoutExtension(targetName));
        if (project.Length == 0)
        {
            project = "build";
        }

        if (project.Length > MaxProjectNameLength)
        {
            project = project.Substring(0, MaxProjectNameLength);
        }

        var name = $"{at:yyyyMMdd-HHmmss-fff}-{kind.ToString().ToLowerInvariant()}-{project}.log";
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString().Trim();
    }
}

/// <summary>
/// Writes the complete output of one run to disk, even the lines the console is
/// not showing (D-0.3.5). A log that cannot be opened is logged and ignored:
/// losing the file must never lose the build.
/// </summary>
public sealed class BuildLogWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    public BuildLogWriter(string path)
    {
        Path = path;

        try
        {
            _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Log.Warning(ex, "Could not open the build log {Path}", path);
            _writer = null;
        }
    }

    public string Path { get; }

    public bool IsOpen => _writer is not null;

    public void WriteLine(string text)
    {
        lock (_gate)
        {
            try
            {
                _writer?.WriteLine(text);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A closed file is not a build failure.
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Closes the file. Flushing alone is not enough: the next run may want to
    /// read or replace it, and on Windows an open handle blocks both.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>
/// One output path: the file gets every line, the problems list gets every line,
/// the console gets every line. Keeping them together is what makes "the log has
/// what the screen dropped" true by construction rather than by remembering to
/// write three calls.
/// </summary>
public sealed class BuildOutputPump : IProgress<ProcessOutputLine>
{
    private readonly IProgress<ProcessOutputLine>? _console;
    private readonly BuildLogWriter _log;
    private readonly BuildProblemCollector _problems;
    private readonly TimeProvider _time;

    public BuildOutputPump(
        IProgress<ProcessOutputLine>? console,
        BuildLogWriter log,
        BuildProblemCollector problems,
        TimeProvider? time = null)
    {
        _console = console;
        _log = log;
        _problems = problems;
        _time = time ?? TimeProvider.System;
    }

    public void Report(ProcessOutputLine value)
    {
        _log.WriteLine(value.Text);
        _problems.Add(value.Text);
        _console?.Report(value);
    }

    /// <summary>Writes a line this app produced (command, folder, hint) to the file and the console.</summary>
    public void WriteLine(string text) =>
        Report(new ProcessOutputLine(text, false, _time.GetUtcNow()));
}
