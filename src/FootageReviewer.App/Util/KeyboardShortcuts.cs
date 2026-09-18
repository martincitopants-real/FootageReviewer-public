using System.Collections.Generic;
using System.Linq;

namespace FootageReviewer.App.Util;

/// <summary>One keyboard shortcut row. <see cref="Keys"/>/<see cref="Description"/> feed the Settings
/// table and the toolbar tooltip; <see cref="Short"/> (when set) feeds the compact bottom legend.</summary>
public sealed class KeyboardShortcut
{
    public string Keys { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Short { get; init; }

    public KeyboardShortcut() { }
    public KeyboardShortcut(string keys, string description, string? @short)
    {
        Keys = keys; Description = description; Short = @short;
    }
}

/// <summary>The single source of truth for every hotkey. Both the Settings → "Keyboard shortcuts"
/// table and the toolbar legend/tooltip are built from this list, so they can never drift apart.</summary>
public static class KeyboardShortcuts
{
    public static readonly IReadOnlyList<KeyboardShortcut> All = new[]
    {
        new KeyboardShortcut("Space", "Play / pause", "Space play"),
        new KeyboardShortcut("← / →", "Skip back / forward (distance set in Playback settings)", "←/→ skip"),
        new KeyboardShortcut("Ctrl + ← / →", "Jump to previous / next audio segment", "Ctrl+←/→ segment"),
        new KeyboardShortcut("1 – 0", "Playback speed 1.0× → 3.25× in 0.25× steps", "1–0 speed"),
        new KeyboardShortcut("Shift + 2 – 0", "Fast speed 2× / 3× / 4× / 6× / 8× / 12× / 16× / 24× / 32×", "Shift+# fast"),
        new KeyboardShortcut(", / .", "Slow down / speed up by 0.25× (Shift = 1× steps; can go below 1×)", ",/. speed"),
        new KeyboardShortcut("T", "New log (focus the entry box)", "T log"),
        new KeyboardShortcut("E", "Edit the last log (Enter resumes playback if it was playing)", "E edit"),
        new KeyboardShortcut("J", "Jump to the most recent log", "J last log"),
        new KeyboardShortcut("D", "Dictate — record the mic, then transcribe it into a log", "D dictate"),
        new KeyboardShortcut("M", "Drop / return to a temporary marker", "M marker"),
        new KeyboardShortcut("Shift + M", "Remove the temporary marker", "Shift+M unmark"),
        new KeyboardShortcut("L", "Lock the timeline view to the playhead (centre-lock)", "L lock view"),
        new KeyboardShortcut("Ctrl + 1 / 2 / 3", "Copy the last transcript bubble (track 1 / 2 / 3) into the log", "Ctrl+1/2/3 copy"),
        new KeyboardShortcut("Ctrl + Shift + 1 / 2 / 3", "Log the last transcript bubble (track 1 / 2 / 3) immediately", "Ctrl+Shift+1/2/3 log"),
        new KeyboardShortcut("X", "Select a section of footage (wheel resizes it, C copies it for Premiere, Esc closes)", "X section"),
        new KeyboardShortcut("C", "Copy the selected section for Premiere (while the section tool is open)", null),
        new KeyboardShortcut("Play/Pause key", "Play / pause even when the app isn't focused (media key)", null),
        new KeyboardShortcut("Ctrl + S", "Save project (Save As if new)", "Ctrl+S save"),
        new KeyboardShortcut("Ctrl + L", "Lock zoom between the video + transcript timelines", "Ctrl+L zoom-lock"),
        new KeyboardShortcut("Alt + scroll", "Zoom the timeline under the cursor", "Alt+scroll zoom"),
        new KeyboardShortcut("Scroll", "Pan the timeline under the cursor", null),
        new KeyboardShortcut("Drag", "Scrub the playhead", null),
    };

    /// <summary>Compact one-line legend for the bottom transport bar.</summary>
    public static string InlineLegend()
        => string.Join("  ·  ", All.Where(s => !string.IsNullOrEmpty(s.Short)).Select(s => s.Short));

    /// <summary>Full "Keys — Description" legend for the legend's tooltip.</summary>
    public static string FullLegend()
        => string.Join("\n", All.Select(s => $"{s.Keys}  —  {s.Description}"));
}
