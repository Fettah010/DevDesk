using System.IO;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace DevDesk.Services;

/// <summary>
/// The file sink's formatter: renders the usual one-line template and then
/// redacts it, so nothing secret is ever written to disk in the first place
/// (roadmap §6.5.2).
/// <para>
/// Redacting on the way out (here and in the ring) is better than redacting on
/// the way in: the export bundle, the Diagnostics page and a human reading the
/// log file all see the same safe text, and no future surface has to remember
/// to scrub anything.
/// </para>
/// </summary>
public sealed class RedactingTextFormatter : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _inner;

    public RedactingTextFormatter(string outputTemplate) => _inner = new MessageTemplateTextFormatter(outputTemplate);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);
        output.Write(LogRedaction.Redact(buffer.ToString()));
    }
}
