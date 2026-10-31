using System.Text.RegularExpressions;

namespace DevDesk.Services;

/// <summary>
/// Turns raw tool output into the text a console should show (W-0.3.4).
/// <para>
/// Two things are removed, and only two: ANSI escape sequences (a compiler that
/// detects a colour-capable pipe paints SGR codes into the stream) and the
/// carriage-return progress rewrites a terminal would overwrite in place. The
/// words are never touched — a filter that also "tidies" the message would hide
/// the text the user came to read.
/// </para>
/// </summary>
public static class AnsiText
{
    /// <summary>CSI sequences: colour, cursor movement, private modes (<c>ESC[?25l</c>).</summary>
    private static readonly Regex Csi = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled);

    /// <summary>OSC sequences: window titles and hyperlinks, terminated by BEL or <c>ESC \</c>.</summary>
    private static readonly Regex Osc = new(
        @"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)",
        RegexOptions.Compiled);

    /// <summary>Any other two-character escape (ESC followed by a single final byte).</summary>
    private static readonly Regex OtherEscape = new(
        @"\x1B[@-Z\\-_]",
        RegexOptions.Compiled);

    /// <summary>The bell, which a redirected stream has no business emitting.</summary>
    private static readonly Regex Bell = new("\x07", RegexOptions.Compiled);

    /// <summary>Removes escape sequences and control bells; keeps every visible character.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = Osc.Replace(text, string.Empty);
        cleaned = Csi.Replace(cleaned, string.Empty);
        cleaned = OtherEscape.Replace(cleaned, string.Empty);
        return Bell.Replace(cleaned, string.Empty);
    }

    /// <summary>
    /// Collapses a line that rewrote itself with <c>\r</c>: a terminal shows the
    /// last segment, so that is what the console shows. A trailing <c>\r</c> does
    /// not erase the text before it.
    /// </summary>
    public static string CollapseCarriageReturns(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\r'))
        {
            return text ?? string.Empty;
        }

        var segments = text.Split('\r');
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (segments[index].Length > 0)
            {
                return segments[index];
            }
        }

        return string.Empty;
    }

    /// <summary>Both cleanups, in the order a console applies them.</summary>
    public static string ForConsole(string? text) => CollapseCarriageReturns(Clean(text));
}
