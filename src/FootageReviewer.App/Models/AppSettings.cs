using System.IO;
using System.Text.Json;

namespace FootageReviewer.App.Models;

/// <summary>App-wide preferences, persisted to %LOCALAPPDATA%\FootageReviewer\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Bumped when a default changes in a way existing settings files must be migrated to.</summary>
    public int SettingsVersion { get; set; }

    public bool AutoSaveEnabled { get; set; } = true;
    public int AutoSaveIntervalSeconds { get; set; } = 60;
    public bool ConfirmOnClose { get; set; } = true;
    public bool TranscribeOnLoad { get; set; } = false;

    /// <summary>How far the ← / → arrow keys (and the transport skip buttons) jump in seconds.</summary>
    public double SkipSeconds { get; set; } = 5.0;

    /// <summary>Pause playback when you start typing a manual-log entry.</summary>
    public bool AutoPause { get; set; }

    /// <summary>Show the ±skip guide lines on the timeline.</summary>
    public bool ShowSkipPreview { get; set; }

    // ---- Manual-log "Copy all" options -------------------------------------
    /// <summary>Copy all copies only entries not yet marked copied (struck through); otherwise all.</summary>
    public bool CopyOnlyUncopied { get; set; } = true;
    /// <summary>Include the clip/folder name as a header line.</summary>
    public bool CopyFolderHeaders { get; set; } = true;
    /// <summary>Include the timestamp before each entry's text.</summary>
    public bool CopyTimestamps { get; set; } = true;

    /// <summary>Include day-marker headings above the clip folders in "Copy all".</summary>
    public bool CopyDayHeadings { get; set; } = true;

    /// <summary>Warn before a clip move or insert breaks an in-order sequence.</summary>
    public bool WarnOnOutOfOrderEdit { get; set; } = true;

    // ---- Recent project (auto-open on launch) ------------------------------
    /// <summary>Last opened/saved project file, reopened on launch.</summary>
    public string? LastProjectPath { get; set; }
    /// <summary>Reopen the last project automatically on launch.</summary>
    public bool OpenLastProjectOnLaunch { get; set; }   // default false: show the launcher first
    /// <summary>Most-recently-used project files (newest first) for the File → Open recent menu.</summary>
    public System.Collections.Generic.List<string> RecentProjects { get; set; } = new();
    /// <summary>Cap on the recent list. Effectively unlimited - the launcher shows your whole history.</summary>
    public int MaxRecentProjects { get; set; } = 10000;

    /// <summary>
    /// Per-machine media locations: where footage that a shared project refers to by another
    /// machine's path actually lives here. Learned automatically the first time a project is
    /// relinked on this machine; never written into the project.
    /// </summary>
    public System.Collections.Generic.List<Util.PathRemap> PathRemaps { get; set; } = new();

    // ---- Transcript ---------------------------------------------------------
    /// <summary>Transcript bubble body font size (px).</summary>
    public double TranscriptFontSize { get; set; } = 11;

    /// <summary>Auto-level target peak (0.4–1.0) each speech chunk is normalized toward.</summary>
    public double AutoLevelMax { get; set; } = 0.9;

    /// <summary>Scale the arrow-key/skip-button distance by the current playback speed (2× → 2× skip).</summary>
    public bool ScaleSkipBySpeed { get; set; }

    /// <summary>Per-track "include in Ctrl+Arrow segment skip" toggles, by track index.</summary>
    public bool[]? SegSkipTracks { get; set; }

    /// <summary>New manual-log entries within this many seconds of an existing one merge into it.</summary>
    public double LogMergeWindowSec { get; set; } = 5.0;

    /// <summary>Shift each NEW manual log's time by this many seconds (e.g. -1 → logged 1s earlier). Not
    /// applied to text copied from a transcript bubble.</summary>
    public double NewLogOffsetSec { get; set; }
    /// <summary>Clicking a log entry centres both timelines on it, whatever the playhead-lock state is.</summary>
    public bool ClickLogSnapsView { get; set; } = true;
    /// <summary>Collapse the Logs-tab header buttons + search bar + clip label for more text room.</summary>
    public bool HideLogChrome { get; set; }
    /// <summary>Timeline scrolls back to the playhead whenever it leaves the visible range (not centre-locked).</summary>
    public bool KeepPlayheadVisible { get; set; }

    // ---- Logger spell-check / autocorrect -----------------------------------
    /// <summary>Autocorrect common typos in the log box as you type (one backspace undoes it).</summary>
    public bool AutoCorrectEnabled { get; set; } = true;
    /// <summary>Capitalize the first word of each sentence in the log box.</summary>
    public bool AutoCapitalizeEnabled { get; set; } = true;
    /// <summary>Underline misspelled words in the log box (red squiggle).</summary>
    public bool SpellCheckEnabled { get; set; } = true;
    /// <summary>Words the user marked "Add to dictionary" — never flagged as misspelled.</summary>
    public System.Collections.Generic.List<string> SpellAllowlist { get; set; } = new();

    /// <summary>Microphone device name for dictation (null/empty = the Windows default / first device).</summary>
    public string? DictationMic { get; set; }

    // ---- Transcript bubble → manual-log copy --------------------------------
    /// <summary>Prepend a speaker prefix to Track 2 bubbles copied into the log.</summary>
    public bool Track2PrefixEnabled { get; set; } = true;
    public string Track2Prefix { get; set; } = "Me:";
    /// <summary>Prepend a speaker prefix to Track 3 bubbles copied into the log.</summary>
    public bool Track3PrefixEnabled { get; set; } = true;
    public string Track3Prefix { get; set; } = "Chatter:";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string SettingsPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "FootageReviewer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Opts) ?? new AppSettings();
                return Migrate(loaded);
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    /// <summary>
    /// One-time upgrades for settings files written by older versions. v1 introduces the launcher: existing
    /// installs have OpenLastProjectOnLaunch=true saved from when that was the default, which would opt them
    /// straight past the new home screen, so it's turned off once and the recent-list cap is lifted.
    /// </summary>
    private static AppSettings Migrate(AppSettings s)
    {
        if (s.SettingsVersion < 1)
        {
            s.OpenLastProjectOnLaunch = false;
            if (s.MaxRecentProjects <= 100) s.MaxRecentProjects = 10000;
            s.SettingsVersion = 1;
            s.Save();
        }
        return s;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* best effort */ }
    }
}
