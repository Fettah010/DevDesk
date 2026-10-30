using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace DevDesk.Services;

/// <summary>
/// Central logging setup (Serilog): the console, a daily-rolling file, and the
/// in-memory <see cref="Ring"/> the Diagnostics page reads.
/// <para>
/// Files live under <c>%LocalAppData%\DevDesk\Logs</c>, not next to the
/// executable (roadmap §3.9): an installed build sits in a directory the user may
/// not be allowed to write to, and a log that silently stops being written is
/// worse than none. <c>DEVDESK_DATA_DIR</c> moves both the settings and the logs,
/// which is also what makes an isolated smoke run possible.
/// </para>
/// </summary>
public static class LoggingService
{
    public const string LogDirectoryName = "Logs";
    public const string LogFileName = "applog-.log";
    public const int RetainedFileCount = 14;

    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>The bounded in-memory buffer behind Diagnostics.</summary>
    public static LogRingBufferSink Ring { get; } = new();

    public static ILogger Log { get; private set; } = Serilog.Log.Logger;

    /// <summary>Where log files are written; set by <see cref="Initialize"/>.</summary>
    public static string LogDirectoryPath { get; private set; } = string.Empty;

    public static void Initialize()
    {
        LogDirectoryPath = ResolveLogDirectory();

        Log = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .WriteTo.File(
                path: Path.Combine(LogDirectoryPath, LogFileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                shared: true,
                formatter: new RedactingTextFormatter(OutputTemplate))
            .WriteTo.Sink(Ring)
            .CreateLogger();

        Serilog.Log.Logger = Log;
        Log.Information(
            "Logging initialized. Log path: {LogPath} (last {Count} files kept)",
            LogDirectoryPath,
            RetainedFileCount);

        // The "process start" mark was taken before this method ran and has been
        // waiting in the timeline; it is written now, together with "logging
        // ready", so the log contains the whole startup in the real order.
        StartupTimeline.Current.Mark(StartupTimeline.LoggingReady);
        StartupTimeline.Current.AttachToLog();
    }

    /// <summary>
    /// Resolves the log directory, creating it. Falls back to the temp folder
    /// when the data directory cannot be written, so the promise "there is always
    /// a log" survives a locked or read-only profile.
    /// </summary>
    public static string ResolveLogDirectory()
    {
        var preferred = Path.Combine(SettingsService.ResolveDataDirectory(), LogDirectoryName);

        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var fallback = Path.Combine(Path.GetTempPath(), "DevDesk", LogDirectoryName);
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
