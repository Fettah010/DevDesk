using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DevDesk.Services;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace DevDesk.Tests;

/// <summary>
/// The redacting formatter must not change what a log line looks like — only
/// what it must not say. These tests pin it against Serilog's own formatter, so a
/// template change cannot silently start quoting or escaping the file output.
/// </summary>
public class RedactingTextFormatterTests
{
    public const string Template = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private readonly ITestOutputHelper _output;

    public RedactingTextFormatterTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Formatter_MatchesSerilogsOwnOutput()
    {
        var logEvent = Event("Update source: {Source} (channel {Channel}, token {Token})", "https://github.com/o/r", "beta", false);

        var expected = Format(new MessageTemplateTextFormatter(Template), logEvent);
        var actual = Format(new RedactingTextFormatter(Template), logEvent);

        _output.WriteLine("serilog : " + expected);
        _output.WriteLine("redacted: " + actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Formatter_RedactsWithoutChangingTheRestOfTheLine()
    {
        var logEvent = Event("cloning {Url}", "https://owner:hunter2@github.com/owner/repo.git");

        var actual = Format(new RedactingTextFormatter(Template), logEvent);

        Assert.DoesNotContain("hunter2", actual, StringComparison.Ordinal);
        Assert.Contains("https://***:***@github.com/owner/repo.git", actual, StringComparison.Ordinal);
        Assert.StartsWith("[", actual, StringComparison.Ordinal);
        Assert.Contains("] [INF] cloning ", actual, StringComparison.Ordinal);
    }

    private static string Format(Serilog.Formatting.ITextFormatter formatter, LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, writer);
        return writer.ToString();
    }

    private static LogEvent Event(string template, params object?[] arguments)
    {
        var parsed = new MessageTemplateParser().Parse(template);
        var properties = new List<LogEventProperty>();
        var index = 0;
        foreach (var token in parsed.Tokens)
        {
            if (token is PropertyToken property)
            {
                properties.Add(new LogEventProperty(property.PropertyName, new ScalarValue(arguments[index++])));
            }
        }

        return new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null, parsed, properties);
    }
}
