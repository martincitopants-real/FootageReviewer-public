using System;
using System.IO;

namespace FootageReviewer.App.Util;

/// <summary>
/// Best-effort crash + diagnostics log at %LOCALAPPDATA%\FootageReviewer\diagnostics.log. Logging must never
/// throw (a failure to log can't be allowed to take down the app), so everything is wrapped + swallowed.
/// </summary>
public static class DiagnosticsLogger
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FootageReviewer", "diagnostics.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }

    // ---- UI-thread op ring buffer (feeds the stall watchdog) ----
    private struct UiOp { public long StartTick; public string Name; public long Ms; }
    private static readonly UiOp[] Ops = new UiOp[128];
    private static int _opNext;

    /// <summary>Record that a named UI-thread op ran for <paramref name="ms"/> ms (cheap; no I/O).</summary>
    public static void NoteUiOp(string name, long startTick, long ms)
    {
        var i = System.Threading.Interlocked.Increment(ref _opNext) - 1;
        Ops[i & 127] = new UiOp { StartTick = startTick, Name = name, Ms = ms };
    }

    /// <summary>Run + record an op. Records only if it took at least <paramref name="minMs"/>.</summary>
    public static void TimedUiOp(string name, Action body, long minMs = 3)
    {
        var t0 = Environment.TickCount64;
        try { body(); }
        finally
        {
            var ms = Environment.TickCount64 - t0;
            if (ms >= minMs) NoteUiOp(name, t0, ms);
        }
    }

    /// <summary>The ops that started after <paramref name="sinceTick"/>, oldest first, as one line.</summary>
    public static string OpsSince(long sinceTick, int max = 14)
    {
        var list = new System.Collections.Generic.List<UiOp>();
        var n = Math.Min(_opNext, Ops.Length);
        for (var k = 0; k < n; k++)
        {
            var op = Ops[(_opNext - 1 - k) & 127];
            if (op.StartTick < sinceTick) break;
            list.Add(op);
        }
        list.Reverse();
        if (list.Count == 0) return "(no timed ops)";
        var parts = new System.Collections.Generic.List<string>();
        foreach (var op in list.Count > max ? list.GetRange(list.Count - max, max) : list)
            parts.Add($"{op.Name} {op.Ms}ms");
        return (list.Count > max ? $"...{list.Count - max} more, " : "") + string.Join(", ", parts);
    }

    public static void LogException(string context, Exception ex)
        => Log($"EXCEPTION [{context}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");

    /// <summary>Catch truly-unhandled exceptions (UI thread crashes bubble here too) + unobserved Task faults.</summary>
    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var tag = "AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : "");
            if (e.ExceptionObject is Exception ex) LogException(tag, ex);
            else Log($"{tag} (non-Exception): {e.ExceptionObject}");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogException("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved(); // don't escalate a background-task fault into a process kill
        };
    }
}
