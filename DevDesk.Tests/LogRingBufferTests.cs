using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DevDesk.Services;
using DevDesk.ViewModels;
using Serilog;
using Serilog.Events;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The ring is what makes Diagnostics show this launch without reading a file.
/// It sits inside every log call, so it must keep exactly the last 2000 events,
/// redact on capture, and never throw.
/// </summary>
public class LogRingBufferTests
{
    private static ILogger LoggerFor(LogRingBufferSink sink) =>
        new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    [Fact]
    public void Ring_EvictsOldestAt2000()
    {
        var ring = new LogRingBufferSink();
        var log = LoggerFor(ring);

        for (var index = 1; index <= LogRingBufferSink.Capacity + 100; index++)
        {
            log.Information("event {Index}", index);
        }

        var snapshot = ring.Snapshot();

        Assert.Equal(LogRingBufferSink.Capacity, snapshot.Count);
        Assert.Equal(LogRingBufferSink.Capacity, ring.Count);
        Assert.Equal(LogRingBufferSink.Capacity + 100, ring.Written);
        Assert.Equal(100, ring.Dropped);

        // Oldest first: the first entry kept is event 101, the last is 2100.
        Assert.Contains("event 101", snapshot[0].Message, StringComparison.Ordinal);
        Assert.Contains($"event {LogRingBufferSink.Capacity + 100}", snapshot[^1].Message, StringComparison.Ordinal);
        Assert.Equal(0, ring.Failed);
    }

    [Fact]
    public void Ring_FiltersByLevelAndText()
    {
        var ring = new LogRingBufferSink();
        var log = LoggerFor(ring);

        log.Information("scan started for {Root}", "DevEcosystem");
        log.Debug("git status --porcelain=v2");
        log.Warning("DevTem WinUI 3 is behind upstream");
        log.Error("boom: repository unreadable");
        log.Information("scan finished in {Elapsed} ms", 380);

        var viewModel = new DiagnosticsViewModel(ring);

        Assert.Equal(5, viewModel.Entries.Count);
        Assert.Equal("5 event(s)", viewModel.CountText);

        // Level filter: warnings and errors only.
        viewModel.SelectedLevel = viewModel.Levels.Single(option => option.Minimum == LogEventLevel.Warning);

        Assert.Equal(2, viewModel.Entries.Count);
        Assert.All(viewModel.Entries, entry => Assert.True(entry.Level >= LogEventLevel.Warning));

        // Text filter on top of the level filter.
        viewModel.TextFilter = "boom";

        var only = Assert.Single(viewModel.Entries);
        Assert.Equal(LogEventLevel.Error, only.Level);
        Assert.Contains("boom", only.Message, StringComparison.Ordinal);
        Assert.Equal("1 of 5 event(s) shown", viewModel.CountText);

        // Text filter matches the level column too, and is case-insensitive.
        viewModel.SelectedLevel = viewModel.Levels[0];
        viewModel.TextFilter = "dbg";

        Assert.Single(viewModel.Entries);
        Assert.Equal(LogEventLevel.Debug, viewModel.Entries[0].Level);

        // Clearing the text filter restores everything.
        viewModel.TextFilter = string.Empty;
        Assert.Equal(5, viewModel.Entries.Count);
    }

    [Fact]
    public void Ring_RedactsOnCapture()
    {
        var ring = new LogRingBufferSink();
        var log = LoggerFor(ring);

        log.Information("cloning https://owner:ghp_{Secret}@github.com/owner/repo.git", "abcdefghijklmnopqrstuvwxyz012345");
        log.Information("GET https://api.github.com/user?access_token=abc123&page=2");

        var snapshot = ring.Snapshot();

        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz012345", snapshot[0].Message, StringComparison.Ordinal);
        Assert.Contains("***", snapshot[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", snapshot[1].Message, StringComparison.Ordinal);
        Assert.Contains("page=2", snapshot[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ring_KeepsExceptionTextRedacted()
    {
        var ring = new LogRingBufferSink();
        var log = LoggerFor(ring);

        log.Error(
            new InvalidOperationException("auth failed for https://user:secret@host/repo"),
            "operation failed");

        var entry = Assert.Single(ring.Snapshot());

        Assert.NotNull(entry.Exception);
        Assert.Contains("InvalidOperationException", entry.Exception!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", entry.Exception!, StringComparison.Ordinal);
        Assert.Contains("[ERR]", entry.Line, StringComparison.Ordinal);
        Assert.True(entry.IsProblem);
    }

    [Fact]
    public void Ring_SurvivesASubscriberThatThrows()
    {
        var ring = new LogRingBufferSink();
        var log = LoggerFor(ring);

        ring.Appended += (_, _) => throw new InvalidOperationException("subscriber is broken");

        log.Information("first");
        log.Information("second");

        // The log call must not propagate the subscriber's exception.
        Assert.Equal(2, ring.Count);
        Assert.Equal(2, ring.Failed);
    }

    [Fact]
    public void Ring_RendersTheSameLineAsTheFileSink()
    {
        // "Copy visible" claims to be paste-ready, so a line from Diagnostics must
        // be the same text as the same line in applog-*.log: same timestamp shape,
        // same three-letter level, same unquoted message, same exception block.
        var ring = new LogRingBufferSink();

        var plain = Event("Update source: {Source} (channel {Channel}, token {Token})", "https://github.com/o/r", "beta", false);
        var failed = Event(new InvalidOperationException("repository unreadable"), "scan failed for {Root}", @"C:\Code\projects");

        ring.Emit(plain);
        ring.Emit(failed);

        var entries = ring.Snapshot();
        Assert.Equal(2, entries.Count);

        foreach (var (entry, logEvent) in entries.Zip(new[] { plain, failed }))
        {
            var fileLine = Format(new RedactingTextFormatter(RedactingTextFormatterTests.Template), logEvent);

            Assert.Equal(fileLine.TrimEnd('\r', '\n'), entry.Line.TrimEnd('\r', '\n'));
        }

        // The concrete shape, spelled out: no quotes around the string arguments
        // and .NET's own false (not False).
        Assert.Contains("(channel beta, token false)", entries[0].Line, StringComparison.Ordinal);
        Assert.Contains("[INF] Update source: https://github.com/o/r", entries[0].Line, StringComparison.Ordinal);
    }

    private static string Format(Serilog.Formatting.ITextFormatter formatter, LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, writer);
        return writer.ToString();
    }

    private static LogEvent Event(string template, params object?[] arguments) => Event(null, template, arguments);

    private static LogEvent Event(Exception? exception, string template, params object?[] arguments)
    {
        var parsed = new Serilog.Parsing.MessageTemplateParser().Parse(template);
        var properties = new List<LogEventProperty>();
        var index = 0;

        foreach (var token in parsed.Tokens)
        {
            if (token is Serilog.Parsing.PropertyToken property)
            {
                properties.Add(new LogEventProperty(property.PropertyName, new ScalarValue(arguments[index++])));
            }
        }

        return new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, exception, parsed, properties);
    }

    [Fact]
    public void Ring_ClearEmptiesTheBufferButNotTheCounters()
    {
        var ring = new LogRingBufferSink();
        var log = LoggerFor(ring);

        log.Information("one");
        log.Information("two");
        ring.Clear();

        Assert.Empty(ring.Snapshot());
        Assert.Equal(0, ring.Count);
        Assert.Equal(2, ring.Written);
    }
}
