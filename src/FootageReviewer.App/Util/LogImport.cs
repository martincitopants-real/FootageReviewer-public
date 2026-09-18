using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FootageReviewer.App.Util;

/// <summary>
/// Parses a pasted block of timestamped log lines into (seconds, text) pairs. Handles both the app's own
/// "Copy all" output (H:MM:SS &lt;tab&gt; text) and hand-written logs (e.g. "8:00 meet the dealer"). The leading
/// timecode is colon-separated and read right-to-left as seconds: 3 parts = H:MM:SS; 2 parts are ambiguous
/// (H:MM vs M:SS) so the caller decides via <paramref name="twoPartIsHoursMinutes"/>. Lines without a leading
/// timecode are skipped when they precede the first entry (headers) or appended to the previous entry
/// (wrapped/continuation text).
/// </summary>
public static class LogImport
{
    public sealed class Line
    {
        public double Seconds;
        public string Text = "";
    }

    // <timecode><sep><text>: groups = hh/mm, mm/ss, optional ss, text. Minutes/seconds 1-2 digits, hours 1-3.
    private static readonly Regex TcLine =
        new(@"^\s*(\d{1,3}):(\d{1,2})(?::(\d{1,2}))?[ \t]+(.+?)\s*$", RegexOptions.Compiled);

    // Recorder block headers like "2026-03-13 14-01-42 - 02.01.42PM" lead each recording. They are not
    // timecodes (the date uses '-' not ':'), so they must be skipped — never folded into the previous
    // entry's text — even when several blocks are pasted back-to-back with no blank line between them.
    private static readonly Regex HeaderLine =
        new(@"^\s*\d{4}-\d{1,2}-\d{1,2}\b", RegexOptions.Compiled);

    /// <summary>True if any line uses a TWO-part timecode (H:MM / M:SS — ambiguous). Drives the prompt.</summary>
    public static bool HasTwoPartTimecodes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var m = TcLine.Match(raw);
            if (m.Success && !m.Groups[3].Success) return true; // matched, but no seconds group → 2-part
        }
        return false;
    }

    public static List<Line> Parse(string? text, bool twoPartIsHoursMinutes)
    {
        var result = new List<Line>();
        if (string.IsNullOrEmpty(text)) return result;
        Line? current = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var m = TcLine.Match(raw);
            if (m.Success)
            {
                var a = int.Parse(m.Groups[1].Value);
                var b = int.Parse(m.Groups[2].Value);
                double seconds;
                if (m.Groups[3].Success)                 // H:MM:SS
                    seconds = a * 3600.0 + b * 60.0 + int.Parse(m.Groups[3].Value);
                else if (twoPartIsHoursMinutes)          // H:MM
                    seconds = a * 3600.0 + b * 60.0;
                else                                     // M:SS
                    seconds = a * 60.0 + b;
                current = new Line { Seconds = seconds, Text = m.Groups[4].Value.Trim() };
                result.Add(current);
            }
            else if (string.IsNullOrWhiteSpace(raw))
            {
                current = null;                          // blank line ends a continuation
            }
            else if (HeaderLine.IsMatch(raw))
            {
                current = null;                          // a block header ends the previous entry (and is skipped)
            }
            else if (current != null)
            {
                // A non-timecode line after an entry = wrapped continuation of its text.
                current.Text = (current.Text + " " + raw.Trim()).Trim();
            }
            // else: a non-timecode line before any entry (e.g. a date header) → skip.
        }
        return result;
    }
}
