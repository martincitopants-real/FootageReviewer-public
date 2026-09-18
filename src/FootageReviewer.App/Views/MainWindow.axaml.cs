using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FootageReviewer.App.Controls;
using FootageReviewer.App.Models;
using FootageReviewer.App.Util;
using HanumanInstitute.LibMpv;

namespace FootageReviewer.App.Views;

public partial class MainWindow : Window
{
    private static readonly double[] Speeds = {
        0.25, 0.5, 0.75,
        1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25,
        4.0, 6.0, 8.0, 12.0, 16.0, 24.0, 32.0,
    };

    // Number-row hotkeys: 1 → 1.0×, 2 → 1.25×, …, 9 → 3.0×, 0 → 3.25×.
    private static readonly Dictionary<Key, double> SpeedHotkeys = new()
    {
        [Key.D1] = 1.0,  [Key.D2] = 1.25, [Key.D3] = 1.5,  [Key.D4] = 1.75,
        [Key.D5] = 2.0,  [Key.D6] = 2.25, [Key.D7] = 2.5,  [Key.D8] = 2.75,
        [Key.D9] = 3.0,  [Key.D0] = 3.25,
    };
    // Shift + number row → big speed jumps for fast scrubbing through footage.
    private static readonly Dictionary<Key, double> ShiftSpeedHotkeys = new()
    {
        [Key.D2] = 2.0,  [Key.D3] = 3.0,  [Key.D4] = 4.0,  [Key.D5] = 6.0,
        [Key.D6] = 8.0,  [Key.D7] = 12.0, [Key.D8] = 16.0, [Key.D9] = 24.0,
        [Key.D0] = 32.0,
    };
    // Order matters: PreferByStem ranks candidates by position here, so when one recording exists as
    // both .mp4 and .mkv the .mp4 wins.
    private static readonly string[] VideoExt = { "*.mp4", "*.mkv", "*.mov", "*.m4v", "*.avi", "*.webm", "*.ts" };

    private MpvContext? _mpv;
    private DispatcherTimer? _timer;
    private double _duration;

    // ---- Smooth playhead interpolation ----
    // mpv's playback time is only polled at ~8 Hz (the 120 ms _timer). Driving the timelines straight
    // from that made the centre-locked view scroll in visible 120 ms chunks (waveforms "dancing",
    // jumpy playhead). Instead the slow timer just records an ANCHOR (mpv time + wall-clock + speed +
    // playing?), and a ~60 Hz timer predicts time = anchor + elapsed*speed between polls and drives both
    // timelines from that — so motion is continuous. mpv stays the source of truth; each poll re-anchors.
    private DispatcherTimer? _smoothTimer;
    private double _phAnchorPos;            // mpv playback time at the last poll
    private long _phAnchorWall;             // Environment.TickCount64 (ms) at that poll
    private double _phSpeed = 1.0;          // playback speed at the anchor
    private bool _phPlaying;                // was playback running (not paused) at the anchor
    private double _smoothPos;              // last interpolated time pushed to the timelines

    private double _speed = 1.0;            // canonical playback speed (free value, not tied to the preset box)
    private bool _applyingSpeed;            // guard: SetPlaybackSpeed sets SpeedBox.SelectedIndex → SelectionChanged
    private bool _ctrlHeld;                 // Ctrl currently down → highlight the Ctrl+1/2/3 copy-target bubbles
    private double _logLastCenteredPlayhead = double.NaN; // last playhead the log list auto-centred on

    // Multi-track audio
    private bool _audioConfigured;
    private int[] _audioIds = Array.Empty<int>();
    private double[] _volumes = Array.Empty<double>();
    private bool[] _muted = Array.Empty<bool>();
    private bool[] _solo = Array.Empty<bool>();
    private bool[] _segSkipTrack = Array.Empty<bool>();   // tracks included in Ctrl+Arrow segment skip
    private double[]? _restoreVolumes;   // per-track fader values to apply once audio is (re)configured
    private bool[]? _restoreMuted;       // per-track mute, restored across a seamless clip edit's audio reconfigure
    private bool[]? _restoreSolo;        // per-track solo, restored across a seamless clip edit's audio reconfigure
    private TextBlock? _audioStatus;
    private DispatcherTimer? _audioRebuildTimer;

    // Auto-level: per-track dynamic loudness normalization via a dynaudnorm filter in the audio graph
    // (set once, no runtime rebuilds). _autoChunks is the silence-bounded speech-run map kept only for
    // the timeline VISUAL (waveform reflection + boundary ticks); _autoLevelTarget = dynaudnorm peak.
    private bool[] _autoLevelTrack = Array.Empty<bool>();   // per-track enable (toggled from the track header)
    private double _autoLevelTarget = 0.9;                  // shared target peak (Settings "Auto-level max")
    private const double AutoMaxGain = 8.0;
    private List<(double Start, double End, double Gain)>[] _autoChunks = Array.Empty<List<(double, double, double)>>();
    private ToggleButton[] _autoLevelButtons = Array.Empty<ToggleButton>();   // the per-track "A" toggles
    private bool _legacyAutoLevelAll;   // a legacy project's single global flag → enable every track once sized
    private bool AnyAutoLevel => Array.Exists(_autoLevelTrack, x => x);

    // Background workers — keyed off the currently loaded sources
    private IReadOnlyList<string>? _currentSources;
    private System.Threading.CancellationTokenSource? _extractCts;

    // Extraction progress (thumbnails + waveforms across all clips × tracks)
    private int _extractTotal;
    private int _extractDone;
    private string _extractKind = "thumbnails + waveforms"; // progress-bar label; narrowed during a single-type regen

    // Clip layout (cumulative starts/durations + display names) — drives playhead-priority waveform
    // extraction and the clip-folder layout of the manual log.
    private double[] _clipStarts = Array.Empty<double>();
    private double[] _clipDurations = Array.Empty<double>();
    private string[] _clipNames = Array.Empty<string>();
    // One-shot hook run after the next clip rebuild (insert/reorder) once _clipStarts is the new layout.
    private Action? _afterClipsBuilt;
    // True while an edit-triggered timeline reload is in flight — serializes clip edits so a second edit
    // can't clobber the pending remap hook or snapshot a half-updated layout.
    private bool _clipReloadInFlight;

    // In-memory mirrors of generated assets so we can embed them in the project file. Kept here
    // (rather than only inside the TimelineControl) so save/load can round-trip them.
    private byte[][][]? _clipThumbnailJpegs;   // [clipIdx][frameIdx] = JPG bytes
    private double[][]? _clipThumbnailTimes;   // [clipIdx][frameIdx] = clip-relative seconds
    private float[][][]? _clipWaveforms;       // [clipIdx][trackIdx] = peaks
    // Cache of the base64-encoded asset DTOs so per-log saves don't re-encode all thumbnails/waveforms
    // (the lag). Invalidated (set _assetsDirty) whenever the assets actually change.
    private volatile bool _assetsDirty = true;
    private List<ClipAssetsDto>? _cachedClipAssets;
    private double _lastPlayhead;   // updated each Tick; read by the background worker as a priority hint

    // Manual log + timer offset (transcript stage 1)
    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly ObservableCollection<ClipGroup> _groups = new();
    // Manual-log discrete playhead: snaps to the log/folder nearest the current time.
    private bool _logLockCenter;            // when true, the list auto-scrolls so the current point stays centred
    private bool _adjustingLogScroll;       // guard: a scroll change driven by our own lock-to-centre

    // Logger spell-check + autocorrect.
    private readonly SpellService _spell = new();
    private DispatcherTimer? _spellDebounce;
    private (int start, int len)[] _misspelledSpans = Array.Empty<(int, int)>();

    // Dictation (mic → whisper, transcribed in the background).
    private readonly DictationService _dictation = new();
    private double? _dictationStartT;       // playhead time when recording started (the log's timestamp)
    private bool _dictationBusy;            // guard against re-entrant start/stop
    private bool _dictationWasPlaying;      // was the video playing when dictation paused it? (resume on stop)
    private DispatcherTimer? _dictationDismissTimer; // auto-clears a transient dictation status after 5s
    // Stack of automatic text edits (autocorrect + auto-capitalize) applied at the last word boundary,
    // each undoable by one backspace, in reverse order (cap first, then autocorrect, then a normal delete).
    private readonly List<(int start, string before, string after)> _autoEdits = new();
    private LogEntry? _editResumeEntry;     // entry opened via the "E" hotkey
    private bool _editResumeWasPlaying;     // was the video playing when "E" paused it? (resume on commit)
    private double? _logLineContentY;       // content-Y of the playhead line (top of the next anchor)
    private double _logActiveCenterContentY;// content-Y centre of the active anchor (for lock-to-centre)
    private double _timerOffset;          // legacy single offset; used only when there are no sync points
    // Re-sync points for the on-screen timer. Each anchor maps a video time to the timer reading at that
    // frame; between anchors the offset is constant (= TimerValue − VideoTime). When the on-screen timer
    // pauses (load screen) then resumes, you drop a new anchor and everything after it self-corrects.
    private readonly List<(double VideoTime, double TimerValue)> _offsetAnchors = new();
    private double? _pendingEntryTime;    // captured at first keystroke so the stamp is when it happened
    private bool _pendingFromTranscript;  // true when the pending stamp came from a transcript bubble (no offset)
    private int _lastCopyTrack = -1;      // track of the last bubble copied into the box (for same-track quote merging)
    private double? _tempMarker;          // "M" bookmark: drop once, press M again to jump back + clear
    private double? _restorePlayhead;     // applied once duration is known after opening a project
    private bool _restoreViewportActive;  // keep re-asserting the saved zoom until the user touches a timeline
    private bool _applyingRestore;         // guard: ViewportChanged fired by our own restore, not the user
    private double _restoreCenterTime;     // the saved playhead time to centre the view on during restore
    private double[]? _restoreTrackWeights;   // transcript column widths to apply once tracks are built
    private bool[]? _restoreTrackCollapsed;   // transcript per-track collapse to apply once tracks are built
    private int _restoreSeekTries;         // bounded re-seek attempts so restore can't loop forever
    private long _lastRestoreSeekTick;     // throttle re-seeks (a deep NAS seek mustn't be restarted each tick)
    private bool _restoreSeekSettled;      // playhead has reached the saved time (or attempts exhausted)
    private bool _scrollToFurthestPending; // after opening a project, scroll the log to the furthest-in entry
    private double? _restoreSpeed;   // playback speed to re-apply once the EDL has a duration (free value)
    private (double Pps, double Scroll)? _restoreVideoViewport;
    private (double Pps, double Scroll)? _restoreTranscriptViewport;

    // Project file (Ctrl+S overwrites this; null → prompt for a location, Premiere-style)
    private string? _currentProjectPath;
    private bool _dirty;
    // Set when opening a project — used to drop embedded assets back into place once the clip layout
    // and audio config become available (asynchronously after LoadTimeline).
    private ProjectDto? _pendingProjectAssets;

    // App settings + auto-save
    private AppSettings _settings = AppSettings.Load();
    private DispatcherTimer? _autoSaveTimer;
    private bool _closeConfirmed;

    // Dedicated "save state" indicator — lives in its own toolbar slot so background workers
    // updating ClipInfo / TranscriptStatus can never overwrite it.
    private DateTime? _lastSavedUtc;
    private DispatcherTimer? _saveStatusTimer;

    // Video-column auto-sizing (16:9 at row height). Latched off the moment the user drags.
    private bool _videoWidthLocked;
    private double _lastAutoVideoWidth;

    // Auto transcript — one column per audio track on a vertical TranscriptTimelineControl.
    // Raw transcribed segments (from Whisper) and the silence-merged display segments. The Pause
    // slider sets the silence gap threshold that breaks one bubble into the next.
    private string[] _audioTitles = Array.Empty<string>();
    // Chapter dividers seeded from the source folders at import. Anchored by clip path, so their times
    // are recomputed from the current layout rather than stored.
    private List<DayMarker> _dayMarkers = new();
    // local path -> the path the shared project stores. Filled when a clip is resolved through a
    // per-machine media location (or relinked by hand); consulted on save so the project keeps its
    // canonical paths and never learns where this machine keeps its copy.
    private readonly Dictionary<string, string> _canonicalBySource = new(StringComparer.OrdinalIgnoreCase);
    // The subset actually drawn, in draw order — a marker whose clip was deleted resolves to nothing,
    // so the control's indices track this list rather than _dayMarkers.
    private readonly List<DayMarker> _drawnDayMarkers = new();
    private List<List<TranscriptSegment>> _rawSegmentsByTrack = new();
    private List<List<TranscriptSegment>> _trackSegments = new();       // displayed (merged)
    private TranscriptSegment?[] _activeSegmentPerTrack = Array.Empty<TranscriptSegment?>();
    // Per-track silence segmentation: each audio track has its own pause-duration + level threshold.
    private const double DefaultPauseSec = 1.5;
    private const double DefaultLevel = 0.05;
    private double[] _trackPause = Array.Empty<double>();   // seconds of silence that ends a bubble
    private double[] _trackLevel = Array.Empty<double>();   // waveform level below which = silence
    private readonly List<TextBox> _trackPauseVals = new();   // editable numeric readouts (click to type)
    private readonly List<Slider> _trackPauseSliders = new();
    private const double GapMinSec = 0.2, GapMaxSec = 10.0;   // "Gap length" slider range
    private DispatcherTimer? _mergeDebounce;   // collapse rapid slider drags into ~20 rebuilds/sec
    private bool _lockZoom;
    private CancellationTokenSource? _transcribeCts;
    private ManualResetEventSlim _transcribePause = new(true); // signalled = running
    private bool _transcribePaused;
    private int _transcribeTotal, _transcribeDone;

    // Compact (un-indented) — the project file is gzip-compressed anyway, and the embedded
    // thumbnail/waveform blobs make pretty-printing pure overhead.
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // Bumped each feature batch; shown in the toolbar + window title.
    public const string AppVersion = "1.18.1";

    // Project the launcher asked us to open (null = start empty). Kept until OnOpened, because the
    // heavy work (mpv init, then loading the project) must happen after the window is up so the splash
    // has something to sit over.
    private readonly string? _startupProject;
    private readonly bool _fromLauncher;

    public MainWindow() : this(null, false) { }

    /// <param name="projectPath">Project to open once the window is live; null starts empty.</param>
    /// <param name="fromLauncher">True when the launcher handed over (suppresses the auto-open setting).</param>
    public MainWindow(string? projectPath, bool fromLauncher = true)
    {
        _startupProject = projectPath;
        _fromLauncher = fromLauncher;
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        VersionLabel.Text = "v" + AppVersion;
        Title = $"Footage Reviewer  v{AppVersion}";
        // Build the bottom hotkey legend + its tooltip from the single shared list (same source the
        // Settings → Keyboard shortcuts table uses) so the two can never drift apart.
        HotkeyLegend.Text = Util.KeyboardShortcuts.InlineLegend();
        ToolTip.SetTip(HotkeyLegend, Util.KeyboardShortcuts.FullLegend());
        SpeedBox.ItemsSource = Speeds.Select(s => $"{s:0.##}x").ToArray();
        SpeedBox.SelectedIndex = Array.IndexOf(Speeds, 1.0);

        Opened += OnOpened;
        // Going Home reopens the launcher only after this window is really gone, so the workspace is
        // fully torn down first (and a cancelled close leaves nothing behind).
        Closed += (_, _) =>
        {
            if (!_goingHome) return;
            try { new LauncherWindow(_settings).Show(); }
            catch (Exception ex) { DiagnosticsLogger.LogException("reopen launcher", ex); }
        };
        MenuOpenFootage.Click += async (_, _) => await OpenFootageAsync();
        MenuOpenFolder.Click += async (_, _) => await OpenFolderAsync();
        PlayBtn.Click += (_, _) => TogglePause();
        SpeedBox.SelectionChanged += (_, _) =>
        {
            // Skip the echo when SetPlaybackSpeed is the one setting the index; otherwise a user picking a
            // preset routes through the single speed sink (which sets mpv speed + re-anchors the smooth loop).
            if (_applyingSpeed) return;
            if (_mpv != null && SpeedBox.SelectedIndex >= 0)
                SetPlaybackSpeed(Speeds[SpeedBox.SelectedIndex]);
        };

        Timeline.SeekRequested += t => { StopViewportRestore(); SeekTo(t); PosText.Text = Fmt(t); };

        // Project + transcript stage 1
        MenuNewProject.Click += async (_, _) => await NewProjectAsync();
        MenuSaveProject.Click += async (_, _) => await SaveProjectAsync(forceDialog: false);
        MenuSaveAs.Click += async (_, _) => await SaveProjectAsync(forceDialog: true);
        MenuOpenProject.Click += async (_, _) => await OpenProjectAsync();
        MenuHome.Click += async (_, _) => await GoHomeAsync();
        MenuOpenRecent.SubmenuOpened += (_, _) => RefreshRecentProjectsMenu(); // populate fresh on hover/open
        MenuRenameProject.Click += async (_, _) => await RenameProjectAsync();
        CopyAllBtn.Click += async (_, _) => await CopyAllAsync();
        // 3-dot copy-options menu
        CopyOnlyNewChk.IsChecked = _settings.CopyOnlyUncopied;
        CopyHeadersChk.IsChecked = _settings.CopyFolderHeaders;
        CopyDayHeadingsChk.IsChecked = _settings.CopyDayHeadings;
        CopyTimestampsChk.IsChecked = _settings.CopyTimestamps;
        CopyOnlyNewChk.IsCheckedChanged += (_, _) => { _settings.CopyOnlyUncopied = CopyOnlyNewChk.IsChecked == true; _settings.Save(); };
        CopyHeadersChk.IsCheckedChanged += (_, _) => { _settings.CopyFolderHeaders = CopyHeadersChk.IsChecked == true; _settings.Save(); };
        CopyDayHeadingsChk.IsCheckedChanged += (_, _) => { _settings.CopyDayHeadings = CopyDayHeadingsChk.IsChecked == true; _settings.Save(); };
        CopyTimestampsChk.IsCheckedChanged += (_, _) => { _settings.CopyTimestamps = CopyTimestampsChk.IsChecked == true; _settings.Save(); };
        MarkAllCopiedBtn.Click += (_, _) => MarkAllCopied(true);
        ClearCopiedBtn.Click += (_, _) => MarkAllCopied(false);
        ImportLogsBtn.Click += async (_, _) => await ImportLogsFromTextAsync();
        OrderChronologicallyBtn.Click += async (_, _) =>
        {
            if (TimelineMenuBtn.Flyout is Avalonia.Controls.Flyout tf) tf.Hide();
            await OrderFootageChronologicallyAsync();
        };
        // Logging preferences (⋮ menu): new-log offset, click-snaps-view, hide-chrome.
        NewLogOffsetInput.Value = (decimal)_settings.NewLogOffsetSec;
        NewLogOffsetInput.ValueChanged += (_, _) =>
        { if (NewLogOffsetInput.Value is decimal v) { _settings.NewLogOffsetSec = (double)v; _settings.Save(); } };
        ClickLogSnapsChk.IsChecked = _settings.ClickLogSnapsView;
        ClickLogSnapsChk.IsCheckedChanged += (_, _) => { _settings.ClickLogSnapsView = ClickLogSnapsChk.IsChecked == true; _settings.Save(); };
        HideLogChromeChk.IsChecked = _settings.HideLogChrome;
        HideLogChromeChk.IsCheckedChanged += (_, _) => { _settings.HideLogChrome = HideLogChromeChk.IsChecked == true; _settings.Save(); ApplyLogChrome(); };
        ApplyLogChrome();
        // Refresh the in-tab ⋮ menus from settings each time they open, so they stay synced with the
        // global Settings window (changes there flow back in).
        if (LogMenuBtn.Flyout is Avalonia.Controls.Flyout logFly)
            logFly.Opened += (_, _) => RefreshCopyOptionChecks();
        if (LevelsBtn.Flyout is Avalonia.Controls.Flyout lvlFly)
            lvlFly.Opened += (_, _) => BuildLevelSettings(_audioTitles);
        OffsetSetBtn.Click += (_, _) => SetOffsetFromInput();
        OffsetClearBtn.Click += (_, _) => ClearOffset();
        OffsetInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SetOffsetFromInput(); e.Handled = true; } };
        EntryInput.TextChanged += (_, _) => OnEntryTextChanged();
        // Tunnel (preview) so this runs BEFORE TextBox's own Enter handling — otherwise, with
        // AcceptsReturn=true, the TextBox inserts a newline and marks Enter handled before we see it,
        // and the entry never submits. Shift+Enter is left unhandled here so the newline still works.
        EntryInput.AddHandler(InputElement.KeyDownEvent, OnEntryInputKeyDown, RoutingStrategies.Tunnel);
        // Autocorrect fires on a word boundary (space/punctuation) typed into the log box.
        EntryInput.AddHandler(InputElement.TextInputEvent, OnEntryTextInput, RoutingStrategies.Tunnel);
        // Spell-check: red squiggle overlay + right-click suggestions.
        SpellAdorner.Attach(EntryInput);
        EntryInput.AddHandler(ContextRequestedEvent, OnEntryContextRequested, RoutingStrategies.Tunnel);
        _spellDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _spellDebounce.Tick += (_, _) => { _spellDebounce!.Stop(); RecheckSpelling(); };
        _spell.Ready += () => RecheckSpelling();
        ApplySpellSettings(); // owns allowlist setup + load/clear per settings
        GroupsList.ItemsSource = _groups;
        // Live log count in the "Logs" header.
        _entries.CollectionChanged += (_, _) => UpdateLogCount();
        UpdateLogCount();

        GenerateBtn.Click += async (_, _) => await GenerateTranscriptAsync();
        ClearTranscriptBtn.Click += async (_, _) => await ClearTranscriptAsync();
        PauseTranscriptBtn.Click += (_, _) => ToggleTranscribePause();
        StopTranscriptBtn.Click += (_, _) => StopTranscription();
        ScrollLockBtn.Click += (_, _) =>
        {
            _lockZoom = ScrollLockBtn.IsChecked == true;
            ScrollLockBtn.Content = _lockZoom ? "🔒" : "🔓";
            if (_lockZoom) SyncTranscriptViewportFromVideo();
        };
        VideoCenterLockBtn.Click += (_, _) =>
            Timeline.LockToCenter = VideoCenterLockBtn.IsChecked == true;
        Timeline.SelectionResize += OnSectionResize;          // wheel resizes the section selection
        Timeline.SelectionAnchorMoved += OnSectionAnchorMoved; // …and it follows the cursor
        VideoKeepVisibleBtn.IsChecked = _settings.KeepPlayheadVisible;
        Timeline.KeepPlayheadVisible = _settings.KeepPlayheadVisible;
        VideoKeepVisibleBtn.Click += (_, _) =>
        {
            _settings.KeepPlayheadVisible = VideoKeepVisibleBtn.IsChecked == true;
            Timeline.KeepPlayheadVisible = _settings.KeepPlayheadVisible;
            _settings.Save();
        };
        TranscriptCenterLockBtn.Click += (_, _) =>
            TranscriptTimeline.LockToCenter = TranscriptCenterLockBtn.IsChecked == true;
        LogCenterLockBtn.Click += (_, _) =>
        {
            _logLockCenter = LogCenterLockBtn.IsChecked == true;
            _logLastCenteredPlayhead = double.NaN; // force an immediate centre when enabled
            UpdateLogPlayhead(force: true);
        };
        // Reposition the log playhead line + sticky-folder label as the user scrolls the list.
        // Skip scrolls we caused ourselves (lock-to-centre) to avoid re-entering the layout pass.
        EntriesScroller.ScrollChanged += (_, _) =>
        {
            if (_adjustingLogScroll) return;
            UpdateStickyFolder();
            UpdateLogPlayhead();
        };
        SkipPreviewBtn.Click += (_, _) =>
        {
            UpdateSkipPreview();
            _settings.ShowSkipPreview = SkipPreviewBtn.IsChecked == true;
            _settings.Save();
        };
        AutoPauseBtn.Click += (_, _) =>
        {
            _settings.AutoPause = AutoPauseBtn.IsChecked == true;
            _settings.Save();
        };
        MicBtn.Click += (_, _) => ToggleDictation();
        DictationDismissBtn.Click += (_, _) => SetDictationStatus("");
        Timeline.MarkerRightClicked += OnMarkerRightClicked;
        Timeline.DayMarkerRightClicked += OnDayMarkerRightClicked;
        Timeline.MarkerClicked += OnMarkerClicked;
        Timeline.MarkerMoved += OnMarkerMoved;
        Timeline.ClipMoved += OnClipMoved;

        // Drag-and-drop footage files from Explorer onto the timeline → import at the nearest clip boundary.
        DragDrop.SetAllowDrop(Timeline, true);
        Timeline.AddHandler(DragDrop.DragOverEvent, OnTimelineDragOver);
        Timeline.AddHandler(DragDrop.DropEvent, OnTimelineDrop);

        // Restore persisted UI preferences.
        AutoPauseBtn.IsChecked = _settings.AutoPause;
        SkipPreviewBtn.IsChecked = _settings.ShowSkipPreview;
        UpdateSkipPreview();
        TranscriptTimeline.SetFontSize(_settings.TranscriptFontSize);
        _autoLevelTarget = _settings.AutoLevelMax; // auto-level Max now lives in Settings

        // Both searches scan EVERYTHING (every log entry / every transcript segment across all tracks) and then
        // repaint a non-virtualised list, so running them on every keystroke on a big project floods the UI
        // thread. Debounce to the end of typing, and never let a search fault escape to kill the app.
        TranscriptSearchBox.TextChanged += (_, _) => DebounceTranscriptSearch();
        TranscriptSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SafeSearch("transcript next", () => TranscriptTimeline.ScrollToMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1)); e.Handled = true; }
        };
        SearchNextBtn.Click += (_, _) => SafeSearch("transcript next", () => TranscriptTimeline.ScrollToMatch(1));
        SearchPrevBtn.Click += (_, _) => SafeSearch("transcript prev", () => TranscriptTimeline.ScrollToMatch(-1));
        LogSearchBox.TextChanged += (_, _) => DebounceLogSearch();
        LogSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SafeSearch("log next", () => NavigateLogMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1)); e.Handled = true; }
        };
        LogSearchPrevBtn.Click += (_, _) => SafeSearch("log prev", () => NavigateLogMatch(-1));
        LogSearchNextBtn.Click += (_, _) => SafeSearch("log next", () => NavigateLogMatch(1));
        LogSearchClearBtn.Click += (_, _) => { LogSearchBox.Text = ""; LogSearchBox.Focus(); };
        TranscriptSearchClearBtn.Click += (_, _) => { TranscriptSearchBox.Text = ""; TranscriptSearchBox.Focus(); };
        TranscriptTimeline.SeekRequested += t => { StopViewportRestore(); SeekTo(t); PosText.Text = Fmt(t); };
        TranscriptTimeline.MarkerMoved += OnMarkerMoved; // drag a transcript marker → retime its log (same handler)
        // The bubble ⧉ button (and the "Copy text" context item) quote the text, optionally prepend a
        // per-track speaker prefix, drop it into the log box, and focus it for editing.
        TranscriptTimeline.SegmentCopyRequested += (track, s) => CopyBubbleToLogger(track, s);
        TranscriptTimeline.SegmentLogRequested += s => LogFromSegment(s);
        // Persist transcript column widths after a divider drag (view-state → silent save).
        TranscriptTimeline.LayoutChanged += () => MarkDirty(contentChanged: false);
        // Note: ViewportChanged on the transcript no longer drives the video timeline because the
        // transcript axis is vertical-time while the video axis is horizontal-time — they can't
        // share pixels-per-second directly. The video → transcript direction still works one-way
        // when lock-zoom is on, so dragging the video zoom level also zooms the transcript.
        Timeline.ViewportChanged += (pps, scroll) =>
        {
            if (_applyingRestore) return;           // our own restore, not a user change
            if (_lockZoom) TranscriptTimeline.SetViewport(pps, scroll);
            // Centre-lock fires this every interpolation frame (~60 Hz) during playback. Marking dirty
            // once is enough to persist the latest scroll on the next save — guard so we don't run
            // UpdateTitleDot/UpdateSaveStatus 60×/sec.
            if (_duration > 0 && !_dirty) MarkDirty(contentChanged: false);
        };
        TranscriptTimeline.ViewportChanged += (_, _) =>
        {
            if (_applyingRestore) return;
            if (_duration > 0 && !_dirty) MarkDirty(contentChanged: false);
        };
        // Only a genuine user zoom/pan stops the saved-zoom restore — NOT centre-lock auto-scroll,
        // which fires ViewportChanged every tick during playback.
        Timeline.ViewportUserChanged += StopViewportRestore;
        TranscriptTimeline.ViewportUserChanged += StopViewportRestore;
        // Per-track Pause/Level sliders are created dynamically in BuildTrackControlStrip().

        MenuSettings.Click += async (_, _) => await OpenSettingsDialog();
        Closing += OnWindowClosing;
        Closed += (_, _) => TearEverythingDown();
        ConfigureAutoSaveTimer();

        // Video column auto-sizes to maintain a 16:9 aspect at the current row height — the side
        // panel grows to fill the rest. Once the user drags the splitter we stop overwriting it.
        PreviewRow.LayoutUpdated += (_, _) => AutoSizeVideoColumn();
        // Latch the manual width the moment the splitter is grabbed, so AutoSizeVideoColumn stops
        // snapping column 0 back to 16:9 (which made the preview|panel splitter feel un-draggable).
        // Tunnel so the native video HWND / log scroller can't swallow the press first; DragStarted is a
        // belt-and-suspenders fallback.
        VideoSplitter.AddHandler(InputElement.PointerPressedEvent, (_, _) => _videoWidthLocked = true,
                                 RoutingStrategies.Tunnel);
        VideoSplitter.DragStarted += (_, _) => _videoWidthLocked = true;

        _saveStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveStatusTimer.Tick += (_, _) => UpdateSaveStatus();
        _saveStatusTimer.Start();
        UpdateSaveStatus();

        // Spacebar plays/pauses; arrows frame-step; Ctrl+S saves. Tunnel so it runs before the focused
        // control — the toolbar/transport buttons are Focusable="False" so they don't steal Space.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            // Track Ctrl held → highlight the bubbles Ctrl+1/2/3 would copy (incl. when Ctrl itself is the key).
            SetCtrlHeld(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is Key.LeftCtrl or Key.RightCtrl);
            // Shift while the section tool is open → extend the selection backwards as well as forwards.
            if (_sectionActive)
                SetSectionBothWays(e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Key is Key.LeftShift or Key.RightShift);
            // Refresh CAPS now (the thread's key state is current as we process this key) — corrects the
            // indicator instantly on the first keystroke after the window regains focus, ahead of the poll.
            UpdateCapsIndicator();

            // Ctrl+S works from anywhere, including while typing in the log box.
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _ = SaveProjectAsync(forceDialog: false);
                e.Handled = true;
                return;
            }
            // Ctrl+L toggles zoom-lock between the video and transcript timelines.
            if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ScrollLockBtn.IsChecked = !(ScrollLockBtn.IsChecked == true);
                _lockZoom = ScrollLockBtn.IsChecked == true;
                ScrollLockBtn.Content = _lockZoom ? "🔒" : "🔓";
                if (_lockZoom) SyncTranscriptViewportFromVideo();
                e.Handled = true;
                return;
            }
            // Ctrl+Shift+Z redoes the last undone clip edit (checked before plain Ctrl+Z). Not while typing.
            if (e.Key == Key.Z && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)
                && FocusManager?.GetFocusedElement() is not TextBox)
            {
                RedoClipEdit();
                e.Handled = true;
                return;
            }
            // Ctrl+Z undoes the last clip-sequence edit (reorder / insert / delete / chronological order).
            // Only when NOT typing — the log box keeps its own text editing untouched.
            if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control
                && FocusManager?.GetFocusedElement() is not TextBox)
            {
                UndoClipEdit();
                e.Handled = true;
                return;
            }
            // Ctrl+SHIFT+1/2/3 — same bubble, but logged IMMEDIATELY instead of being parked in the log box.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                var instantTrack = e.Key switch
                {
                    Key.D1 or Key.NumPad1 => 0,
                    Key.D2 or Key.NumPad2 => 1,
                    Key.D3 or Key.NumPad3 => 2,
                    _ => -1,
                };
                var focusedNow = FocusManager?.GetFocusedElement();
                if (instantTrack >= 0 && (focusedNow is not TextBox ftb2 || ReferenceEquals(ftb2, EntryInput)))
                {
                    LogLastBubbleImmediately(instantTrack);
                    e.Handled = true;
                    return;
                }
            }
            // Ctrl+1/2/3 (and the numeric-pad equivalents) copy the last transcript bubble for track
            // 1/2/3 into the log — the same as clicking that bubble's copy button. Placed before the
            // TextBox guard below so it works even while typing in the LOG box. Require Ctrl WITHOUT Alt
            // (AltGr = Ctrl+Alt on many layouts types real characters) and without Shift.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                var copyTrack = e.Key switch
                {
                    Key.D1 or Key.NumPad1 => 0,
                    Key.D2 or Key.NumPad2 => 1,
                    Key.D3 or Key.NumPad3 => 2,
                    _ => -1,
                };
                // Fire from the log box or from outside any text box; don't hijack the offset / search
                // boxes (that would yank focus to the log box and append a bubble there).
                var focused = FocusManager?.GetFocusedElement();
                if (copyTrack >= 0 && (focused is not TextBox ftb || ReferenceEquals(ftb, EntryInput)))
                {
                    CopyLastBubble(copyTrack);
                    e.Handled = true;
                    return;
                }
            }
            // Delete removes the selected clips from the sequence and rebuilds the EDL.
            if (e.Key == Key.Delete && Timeline.SelectedClips.Count > 0)
            {
                DeleteSelectedClips();
                e.Handled = true;
                return;
            }
            if (FocusManager?.GetFocusedElement() is TextBox) return;

            // Shift + number row → big speed jumps (2×–32×) for fast scrubbing. Checked before the
            // plain number-row hotkeys so Shift+2 means 2×, not 1.25×. Shift ONLY (no Ctrl/Alt) so
            // Ctrl+Shift+digit doesn't double as a speed key.
            if (e.KeyModifiers == KeyModifiers.Shift && ShiftSpeedHotkeys.TryGetValue(e.Key, out var fastSpeed))
            {
                SetPlaybackSpeed(fastSpeed);
                e.Handled = true;
                return;
            }
            // Plain number-row hotkeys snap playback speed (no modifiers — Ctrl+digit is reserved: 1/2/3
            // copy bubbles, others do nothing).
            if (e.KeyModifiers == KeyModifiers.None && SpeedHotkeys.TryGetValue(e.Key, out var hotSpeed))
            {
                SetPlaybackSpeed(hotSpeed);
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    TogglePause();
                    e.Handled = true;
                    break;
                case Key.Left:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) SkipToAudioSegment(-1);
                    else SkipSeconds(-EffectiveSkip());
                    e.Handled = true;
                    break;
                case Key.Right:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) SkipToAudioSegment(1);
                    else SkipSeconds(EffectiveSkip());
                    e.Handled = true;
                    break;
                case Key.T:
                    StartManualLog();
                    e.Handled = true;
                    break;
                case Key.E:
                    EditLastLog();
                    e.Handled = true;
                    break;
                case Key.J when e.KeyModifiers == KeyModifiers.None:
                    JumpToMostRecentLog();
                    e.Handled = true;
                    break;
                case Key.L when e.KeyModifiers == KeyModifiers.None:
                    ToggleViewLock();
                    e.Handled = true;
                    break;
                // Shift+M removes the temp marker; plain M drops / returns to it. Exact-modifier guards so
                // stray Ctrl/Alt (e.g. Ctrl held for the copy-target highlight) can't trigger them.
                case Key.M when e.KeyModifiers == KeyModifiers.Shift:
                    ClearTempMarker();
                    e.Handled = true;
                    break;
                case Key.M when e.KeyModifiers == KeyModifiers.None:
                    ToggleTempMarker();
                    e.Handled = true;
                    break;
                // , / . nudge playback speed by 0.25× (Shift = 1×); can go below 1×.
                case Key.OemComma:
                    NudgeSpeed(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1.0 : -0.25);
                    e.Handled = true;
                    break;
                case Key.OemPeriod:
                    NudgeSpeed(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1.0 : 0.25);
                    e.Handled = true;
                    break;
                // (Copy last transcript bubble is Ctrl+1/2/3 — handled above, before the TextBox guard.)
                case Key.D when e.KeyModifiers == KeyModifiers.None:
                    ToggleDictation();
                    e.Handled = true;
                    break;
                // X opens/closes the "copy a section to Premiere" tool; while it's open the mouse wheel over
                // the timeline resizes the selection, C copies it and Esc closes.
                case Key.X when e.KeyModifiers == KeyModifiers.None:
                    ToggleSectionSelect();
                    e.Handled = true;
                    break;
                case Key.C when e.KeyModifiers == KeyModifiers.None && _sectionActive:
                    _ = CopySectionForPremiereAsync();
                    e.Handled = true;
                    break;
                case Key.Escape when _sectionActive:
                    ToggleSectionSelect();
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel);

        // Ctrl released → drop the copy-target highlight.
        AddHandler(KeyUpEvent, (_, e) =>
        {
            var stillCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is not (Key.LeftCtrl or Key.RightCtrl);
            SetCtrlHeld(stillCtrl);
            if (_sectionActive)
            {
                var stillShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key is not (Key.LeftShift or Key.RightShift);
                SetSectionBothWays(stillShift);
            }
        }, RoutingStrategies.Tunnel);
        // Losing focus can swallow the Ctrl KeyUp → clear the highlight defensively.
        Deactivated += (_, _) => { SetCtrlHeld(false); SetSectionBothWays(false); };
    }

    // Ctrl-held state → outline the transcript bubbles that Ctrl+1/2/3 would copy.
    private void SetCtrlHeld(bool held)
    {
        if (held == _ctrlHeld) return;
        _ctrlHeld = held;
        if (held) TranscriptTimeline.HighlightCopyTargets(CurrentTime());
        else TranscriptTimeline.ClearCopyTargets();
    }

    private void SetPlaybackSpeed(double speed)
    {
        if (_mpv == null) return;
        // Free value snapped to a 0.25 grid, clamped to [0.25, 32] — supports the , / . fine control
        // (incl. below 1×) without being limited to the preset dropdown's entries.
        speed = Math.Clamp(Math.Round(speed * 4) / 4, 0.25, 32);
        _speed = speed;
        try { _mpv.Speed.Set(speed); } catch { return; }
        // Re-anchor the smooth interpolator at the NEW rate from where the playhead is now, so it doesn't
        // keep gliding at the old speed until the next 120 ms poll (a hitch on fast jumps).
        _phAnchorPos = _smoothPos;
        _phAnchorWall = Environment.TickCount64;
        _phSpeed = speed;
        // Reflect a matching preset in the dropdown (guarded so it doesn't recurse via SelectionChanged).
        var idx = Array.IndexOf(Speeds, speed);
        if (idx >= 0 && SpeedBox.SelectedIndex != idx)
        {
            _applyingSpeed = true;
            SpeedBox.SelectedIndex = idx;
            _applyingSpeed = false;
        }
        try { _mpv.RunCommandString($"show-text \"{FormatSpeed(speed)}\" 600"); } catch { }
        UpdateSkipPreview(); // guide lines may be speed-scaled
    }

    // , / . nudge: ±0.25× (Shift ±1×). Allows below 1×.
    private void NudgeSpeed(double delta) => SetPlaybackSpeed(_speed + delta);

    private double CurrentSpeed() => _speed;

    // A real user zoom/pan ends the saved-zoom restore (centre-lock auto-scroll does not).
    private void StopViewportRestore()
    {
        _restoreViewportActive = false;
        _restoreVideoViewport = null;
        _restoreTranscriptViewport = null;
    }

    // Effective skip distance: the configured seconds, optionally scaled by the current playback speed.
    private double EffectiveSkip()
        => _settings.SkipSeconds * (_settings.ScaleSkipBySpeed ? CurrentSpeed() : 1.0);

    private static string FormatSpeed(double s)
        => s % 1 == 0 ? $"{s:0}×" : $"{s:0.##}×";

    // Skip the playhead by ±_settings.SkipSeconds (configurable). Shows a YouTube-style mpv OSD
    // overlay so the user sees the jump even if the seek looks like a pause on slow NAS files.
    private void SkipSeconds(double delta)
    {
        if (_mpv == null || _duration <= 0) return;
        StopViewportRestore();
        var cur = CurrentTime();
        var target = Math.Clamp(cur + delta, 0, _duration);
        SeekTo(target);
        PosText.Text = Fmt(target);
        try
        {
            var glyph = delta < 0
                ? $"⏪  {Math.Abs(delta):0.#}s"        // ⏪
                : $"{delta:0.#}s  ⏩";                 // ⏩
            _mpv.RunCommandString($"show-text \"{glyph}\" 500");
        }
        catch { }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _mpv = Video.MpvContext;
        if (_mpv != null)
        {
            // Route libmpv's own diagnostics to a file next to ours — the only way to see why a load
            // fails inside mpv (bad EDL, no video output, codec refusal) rather than guessing.
            try
            {
                var mpvLog = Path.Combine(Path.GetDirectoryName(DiagnosticsLogger.LogPath)!, "mpv.log");
                _mpv.SetPropertyString("log-file", mpvLog);
                _mpv.SetPropertyString("msg-level", "all=warn");
            }
            catch (Exception ex) { DiagnosticsLogger.Log("mpv log-file setup failed: " + ex.Message); }
            _mpv.SetPropertyString("hr-seek", "yes");
            _mpv.SetPropertyString("keep-open", "yes");
            _mpv.SetPropertyString("audio-pitch-correction", "yes");
            _mpv.SetPropertyString("af", "scaletempo2=max-speed=32");
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // ~60 Hz interpolation timer — predicts the playhead between the slow mpv polls so the
        // centre-locked view scrolls smoothly instead of in 120 ms steps. Cheap when paused/idle
        // (SetPlayhead early-returns when the value is unchanged → no invalidation).
        _smoothTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _smoothTimer.Tick += (_, _) => SmoothTick();
        _smoothTimer.Start();

        // Keyboard media Play/Pause works even when the app isn't focused, so playback can be toggled while
        // tabbed out (e.g. writing notes in another window).
        GlobalMediaKey.Install(() => Dispatcher.UIThread.Post(TogglePause));

        // Open whatever we were handed. The launcher passes an explicit project; a direct start (auto-open
        // enabled, launcher skipped) falls back to the remembered one.
        if (!string.IsNullOrEmpty(_startupProject))
        {
            ShowSplash(Path.GetFileNameWithoutExtension(_startupProject), "Opening project…");
            _ = OpenProjectFromPathAsync(_startupProject!);
        }
        else if (!_fromLauncher && _settings.OpenLastProjectOnLaunch)
        {
            var last = _settings.LastProjectPath;
            if (!string.IsNullOrEmpty(last)) ShowSplash(Path.GetFileNameWithoutExtension(last), "Opening project…");
            _ = TryOpenLastProjectAsync();
        }

        // "Auto-generate on load" (Settings -> Transcript). The checkbox has always been saved but
        // nothing ever acted on it, so the setting silently did nothing.
        if (_settings.TranscribeOnLoad) _ = AutoGenerateTranscriptWhenReadyAsync();
    }

    // UI-stall watchdog: this timer fires every 120 ms, so a much larger gap means something blocked the UI
    // thread. Logs the offender's tag so stutters can be traced from diagnostics.log.
    private long _lastTickWall;
    private double _lastGcPauseMs;
    private string _diagLastOp = "";
    private void NoteHeavyOp(string op) => _diagLastOp = op;

    private void Tick()
    {
        if (_mpv == null) return;
        var tickStart = Environment.TickCount64;
        try { TickCore(); }
        finally
        {
            var ms = Environment.TickCount64 - tickStart;
            if (ms >= 5) DiagnosticsLogger.NoteUiOp("Tick", tickStart, ms);
        }
    }

    private void TickCore()
    {

        var nowWall = Environment.TickCount64;
        if (_lastTickWall != 0)
        {
            var gap = nowWall - _lastTickWall;
            if (gap > 400)
            {
                var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
                DiagnosticsLogger.Log($"UI stall {gap}ms: ops in window = [{DiagnosticsLogger.OpsSince(_lastTickWall)}]; " +
                    $"GC pause {pause - _lastGcPauseMs:0} ms, gen2 {GC.CollectionCount(2)}, heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            }
            _lastGcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        }
        _lastTickWall = nowWall;

        var mpvT0 = Environment.TickCount64;
        var dur = MpvNum("duration") ?? 0;
        if (Math.Abs(dur - _duration) > 0.001)
        {
            _duration = dur;
            DurText.Text = "/ " + Fmt(dur);
            Timeline.SetTotal(dur);
            TranscriptTimeline.SetTotal(dur);
        }

        var pos = MpvNum("playback-time") ?? 0;
        _lastPlayhead = pos;
        { var mpvMs = Environment.TickCount64 - mpvT0; if (mpvMs >= 5) DiagnosticsLogger.NoteUiOp("Tick.mpvReads", mpvT0, mpvMs); }
        PosText.Text = Fmt(pos);
        PercentText.Text = _duration > 0 ? $"{Math.Clamp(pos / _duration * 100, 0, 100):0.0}%" : "";
        // Re-anchor the smooth interpolator to this authoritative mpv sample. The timeline playhead is
        // driven by SmoothTick (~60 Hz) from this anchor, NOT pushed here — pushing at 8 Hz is exactly
        // the chunky motion we're removing.
        _phAnchorPos = pos;
        _phAnchorWall = Environment.TickCount64;
        _phSpeed = CurrentSpeed();
        _phPlaying = IsPlaying();
        HighlightTranscriptAt(pos);
        UpdateNearPlayheadLogs(pos); // violet-highlight the logs the playhead is sitting within
        UpdateLogPlayhead();   // snap the manual-log playhead line to the current time
        if (_logLockCenter) UpdateStickyFolder(); // locked auto-scroll bypasses ScrollChanged
        UpdateCapsIndicator();
        if (_ctrlHeld) TranscriptTimeline.HighlightCopyTargets(CurrentTime()); // keep targets fresh as playback moves

        if (!_audioConfigured) TryConfigureAudio();

        // Restore playhead/speed after opening a project, once the EDL has a known duration.
        if (_duration > 0)
        {
            if (_restoreSpeed is double rs)
            {
                SetPlaybackSpeed(rs); // free value (also updates the dropdown when it matches a preset)
                _restoreSpeed = null;
            }
            if (_restorePlayhead is double rp)
            {
                SeekTo(rp);
                try { _mpv.Pause.Set(true); } catch { /* ignore */ }
                _restorePlayhead = null;
            }
            // While the restore is active, keep nudging the playhead to the saved time until it lands
            // (an early seek can be dropped before the EDL is fully ready), so the view + playhead agree.
            // Clamp to the actual duration (a saved time past a now-shorter EDL would never settle) and
            // cap the attempts so we can't re-seek forever if the user just leaves it paused.
            // A deep seek (e.g. 50 h into a NAS edl:// chain) can take many seconds to land. Re-issue the
            // seek at most once a second (re-issuing every 120ms would restart it and it'd never settle),
            // up to ~30 s, stopping as soon as the playhead reaches the saved time.
            if (_restoreViewportActive && !_restoreSeekSettled)
            {
                var target = Math.Clamp(_restoreCenterTime, 0, _duration);
                if (Math.Abs(pos - target) <= 0.5) _restoreSeekSettled = true;
                else if (_restoreSeekTries >= 30) _restoreSeekSettled = true;
                else if (Environment.TickCount64 - _lastRestoreSeekTick > 1000)
                {
                    SeekTo(target);
                    _restoreSeekTries++;
                    _lastRestoreSeekTick = Environment.TickCount64;
                }
            }
            // Restore saved zoom/scroll for both timelines. Re-assert EVERY tick until the user touches
            // a timeline — on launch the controls auto-fit (zoom out) on later renders as media/assets
            // finish loading, which would otherwise wipe the saved zoom. SetViewport early-returns when
            // unchanged, so this is a cheap no-op once it's stable. _applyingRestore tells the
            // ViewportChanged handlers this change is ours (not a user zoom), so it doesn't deactivate.
            if (_restoreViewportActive)
            {
                _applyingRestore = true;
                // Ensure the timelines know the real (mpv EDL) duration before we centre. SetClips can
                // post a 0 ffprobe total (av:// / failed probe); with _total == 0, CenterOn early-returns
                // and ClampScroll snaps scroll to 0 — that's why the view landed at the start. SetTotal is
                // a cheap no-op when unchanged.
                Timeline.SetTotal(_duration);
                TranscriptTimeline.SetTotal(_duration);
                // Re-assert the saved zoom (pps) and centre on the SAVED time so the user lands where they
                // left off — not at the start. Centre UNCONDITIONALLY (even when centre-lock is on): a
                // centre-locked timeline normally follows the live mpv playhead, but on launch that's still
                // ~0 until the deep restore-seek lands, so deferring to it pinned the view to the start.
                // CenterOn ignores _playhead and doesn't raise ViewportChanged; the restore ends the moment
                // the user pans/zooms/seeks (StopViewportRestore), after which normal centre-lock resumes.
                if (_restoreVideoViewport is { } vv)
                {
                    Timeline.SetViewport(vv.Pps, Timeline.ScrollSeconds);
                    Timeline.CenterOn(_restoreCenterTime);
                }
                if (_restoreTranscriptViewport is { } tv)
                {
                    TranscriptTimeline.SetViewport(tv.Pps, TranscriptTimeline.ScrollSeconds);
                    TranscriptTimeline.CenterOn(_restoreCenterTime);
                }
                _applyingRestore = false;
            }
        }
    }

    // 60 Hz: predict the playhead between the slow mpv polls and drive both timelines smoothly. mpv stays
    // the source of truth (each 120 ms poll re-anchors); this just fills the gaps so the centre-locked
    // view scrolls fluidly instead of in 120 ms steps.
    private void SmoothTick()
    {
        var st0 = Environment.TickCount64;
        try { SmoothTickCore(); }
        finally { var ms = Environment.TickCount64 - st0; if (ms >= 5) DiagnosticsLogger.NoteUiOp("SmoothTick", st0, ms); }
    }

    private void SmoothTickCore()
    {
        if (_mpv == null || _duration <= 0) return;
        if (_restoreViewportActive) return; // the launch restore owns the view until the user takes over

        double target;
        if (_phPlaying)
        {
            var elapsed = (Environment.TickCount64 - _phAnchorWall) / 1000.0;
            target = _phAnchorPos + elapsed * _phSpeed;
            // Don't run away if mpv stalls between polls (deep seek / dropped frames): cap how far past
            // the last anchor we'll predict before the next poll corrects us.
            var cap = _phAnchorPos + Math.Abs(_phSpeed) * 0.30 + 0.05;
            if (target > cap) target = cap;
        }
        else target = _phAnchorPos;
        target = Math.Clamp(target, 0, _duration);

        var d = target - _smoothPos;
        if (Math.Abs(d) > 0.75) _smoothPos = target;            // big jump = seek / loop / re-anchor → snap
        else if (_phPlaying && d < 0) _smoothPos += d * 0.5;    // prediction overshot mpv: ease back gently
                                                                // (no hard freeze-stutter, no visible jump)
        else _smoothPos = target;                               // normal forward prediction — smooth, zero lag

        // Order matches the old 8 Hz path: video first (its centre-lock may mirror zoom to the transcript
        // when lock-zoom is on), then the transcript's own centre-lock has the final say on its scroll.
        Timeline.SetPlayhead(_smoothPos);
        TranscriptTimeline.SetPlayhead(_smoothPos);
    }

    // ---- Multi-track audio -------------------------------------------------

    private void TryConfigureAudio()
    {
        var count = TryGetInt("track-list/count") ?? 0;
        if (count <= 0) return; // file not parsed yet

        var ids = new List<int>();
        var titles = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (GetStr($"track-list/{i}/type") != "audio") continue;
            ids.Add(TryGetInt($"track-list/{i}/id") ?? ids.Count + 1);
            // Always label tracks "Track 1/2/3" — the file's embedded title (e.g. "Gameplay") is
            // unreliable across recordings and we now transcribe every track regardless.
            titles.Add($"Track {ids.Count}");
        }

        _audioConfigured = true;
        _audioIds = ids.ToArray();
        _audioTitles = titles.ToArray();
        // Fires once per load. Audio track count comes from mpv, so a line here proves mpv parsed the
        // EDL — if it never appears, playback/waveforms/transcript are all dead upstream of ffmpeg.
        DiagnosticsLogger.Log($"mpv loaded: {count} tracks in track-list, {_audioIds.Length} audio");
        _volumes = Enumerable.Repeat(1.0, ids.Count).ToArray();
        if (_restoreVolumes != null)
        {
            for (var i = 0; i < ids.Count && i < _restoreVolumes.Length; i++) _volumes[i] = _restoreVolumes[i];
            _restoreVolumes = null;
        }
        _muted = new bool[ids.Count];
        _solo = new bool[ids.Count];
        // Carry per-track mute/solo across a seamless clip edit's audio reconfigure (else a reorder would
        // silently unmute/unsolo every track).
        if (_restoreMuted != null) { for (var i = 0; i < ids.Count && i < _restoreMuted.Length; i++) _muted[i] = _restoreMuted[i]; _restoreMuted = null; }
        if (_restoreSolo != null) { for (var i = 0; i < ids.Count && i < _restoreSolo.Length; i++) _solo[i] = _restoreSolo[i]; _restoreSolo = null; }
        // Segment-skip inclusion: restore from settings (by index), default true for any new track.
        _segSkipTrack = Enumerable.Range(0, ids.Count)
            .Select(i => _settings.SegSkipTracks == null || i >= _settings.SegSkipTracks.Length || _settings.SegSkipTracks[i])
            .ToArray();
        EnsureAutoLevelArrays(ids.Count);
        // Apply a legacy project's global auto-level flag now that we know the real track count.
        if (_legacyAutoLevelAll) { Array.Fill(_autoLevelTrack, true); _legacyAutoLevelAll = false; }

        var swCfg = System.Diagnostics.Stopwatch.StartNew();
        BuildTrackHeaders(titles);            var msHeaders = swCfg.ElapsedMilliseconds;
        Timeline.SetTracks(ids.Count);        var msTracks = swCfg.ElapsedMilliseconds - msHeaders;
        InitTranscriptForTracks(titles);      var msInitTx = swCfg.ElapsedMilliseconds - msHeaders - msTracks;
        RebuildAudioGraph();                  var msGraph = swCfg.ElapsedMilliseconds - msHeaders - msTracks - msInitTx;
        if (swCfg.ElapsedMilliseconds > 150)
            DiagnosticsLogger.Log($"audio configure: headers {msHeaders} ms, tracks {msTracks} ms, initTranscript {msInitTx} ms, audioGraph {msGraph} ms");
        // The embedded-asset decode may still be running on its background task. The restore AND the
        // "what's missing?" decision below must wait for it, or we'd regenerate every waveform that was
        // about to be restored from the project file.
        var trackIds = ids.Count;
        RunWhenAssetsDecoded(() =>
        {
            if (_audioIds.Length != trackIds) return; // audio config changed underneath us; its own call handles it
            var swTail = System.Diagnostics.Stopwatch.StartNew();
            TryRestoreEmbeddedAssets();           var msRestore = swTail.ElapsedMilliseconds;
            PushInMemoryWaveforms(); // display any carried/in-memory waveforms now that the track count is known
            var msPush = swTail.ElapsedMilliseconds - msRestore;
            if (swTail.ElapsedMilliseconds > 150) DiagnosticsLogger.Log($"audio tail: restore {msRestore} ms, pushWaveforms {msPush} ms");

            // Extract only what's actually MISSING: a seamless reorder/insert carries the existing clips'
            // waveforms in memory, so only genuinely-new (or never-extracted) clip/track pairs get processed.
            var anyMissing = _currentSources != null && (_clipWaveforms == null ||
                Enumerable.Range(0, _currentSources.Count).Any(c =>
                    c >= _clipWaveforms.Length || _clipWaveforms[c] == null ||
                    Enumerable.Range(0, trackIds).Any(t => t >= _clipWaveforms[c]!.Length || _clipWaveforms[c]![t] == null)));
            if (_currentSources != null && anyMissing)
                StartBackgroundExtraction(_currentSources, trackIds);
        });
    }

    // Push UI state to the timeline immediately (instant dimming + waveform scaling) and schedule a
    // graph rebuild on a short trailing-edge debounce so a slider drag doesn't fire ~60 rebuilds/sec.
    // af-command can't reach filters declared inside lavfi-complex, so the only reliable way to make
    // per-track Mute/Solo/Volume audible is to bake the gains into the graph and re-apply it.
    private void ApplyAudioGains()
    {
        if (_audioIds.Length == 0) return;
        for (var i = 0; i < _audioIds.Length; i++)
            Timeline.SetTrackState(i, _volumes[i], _muted[i], _solo[i]);

        _audioRebuildTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _audioRebuildTimer.Tick -= OnAudioRebuildTick;
        _audioRebuildTimer.Tick += OnAudioRebuildTick;
        _audioRebuildTimer.Stop();
        _audioRebuildTimer.Start();
    }

    private void OnAudioRebuildTick(object? sender, EventArgs e)
    {
        _audioRebuildTimer?.Stop();
        RebuildAudioGraph();
    }

    private void RebuildAudioGraph()
    {
        var swGraph = System.Diagnostics.Stopwatch.StartNew();
        try
        {
        if (_mpv == null || _audioIds.Length == 0) return;

        var anySolo = _solo.Any(s => s);
        var gains = new double[_audioIds.Length];
        for (var i = 0; i < _audioIds.Length; i++)
        {
            var g = _volumes[i];
            if (_muted[i]) g = 0;
            if (anySolo && !_solo[i]) g = 0;
            gains[i] = g;
        }

        // Per-track filter chain: volume, then (when auto-level is on for an audible track) a single
        // dynaudnorm dynamic normalizer. dynaudnorm is set once and varies its gain internally, so we
        // NEVER rebuild the graph during playback — that runtime rebuild was what skipped the audio at
        // each speech segment. The graph only rebuilds on user actions (toggle / volume / mute).
        string Chain(int i)
        {
            // Order matters: dynaudnorm (auto-level) FIRST so the track is normalized, THEN the volume
            // fader scales that uniformly — otherwise dynaudnorm would re-normalize away the fader.
            var on = i < _autoLevelTrack.Length && _autoLevelTrack[i] && gains[i] > 0;
            var dyn = on ? $"dynaudnorm=p={Inv(_autoLevelTarget)}:m={Inv(AutoMaxGain)}:f=250:g=15," : "";
            return $"[aid{_audioIds[i]}]{dyn}volume=volume={Inv(gains[i])}";
        }

        var sb = new StringBuilder();
        if (_audioIds.Length == 1)
        {
            sb.Append($"{Chain(0)}[ao]");
        }
        else
        {
            for (var i = 0; i < _audioIds.Length; i++)
                sb.Append($"{Chain(i)}[a{i}];");
            for (var i = 0; i < _audioIds.Length; i++)
                sb.Append($"[a{i}]");
            sb.Append($"amix=inputs={_audioIds.Length}:normalize=0[ao]");
        }

        try
        {
            _mpv.SetPropertyString("lavfi-complex", sb.ToString());
            if (_audioStatus != null) _audioStatus.Text = $"{_audioIds.Length} tracks ✓";
        }
        catch (Exception ex)
        {
            if (_audioStatus != null) _audioStatus.Text = "mix error: " + ex.Message;
        }
        }
        finally { DiagnosticsLogger.NoteUiOp("RebuildAudioGraph", Environment.TickCount64 - swGraph.ElapsedMilliseconds, swGraph.ElapsedMilliseconds); }
    }

    // ---- Auto-level (per-chunk loudness normalization) --------------------

    // Recompute per-track chunks + gains from the waveforms (a chunk = a silence-bounded speech run,
    // the same detection the transcript uses). Each chunk's gain lifts its loudest sample to the
    // target, capped to avoid blowing up near-silent chunks. Pushes the chunks to the timeline for
    // the waveform reflection + boundary markers, then refreshes the live gain.
    // Grow/seed the per-track auto-level enable array, preserving existing toggles.
    private void EnsureAutoLevelArrays(int n)
    {
        if (_autoLevelTrack.Length == n) return;
        var grown = new bool[n];
        Array.Copy(_autoLevelTrack, grown, Math.Min(_autoLevelTrack.Length, n));
        _autoLevelTrack = grown;
    }

    // Recompute the per-track speech chunks purely for the VISUAL (waveform reflection + boundary
    // ticks on the timeline) and rebuild the audio graph so dynaudnorm is added/removed. The audio
    // boost itself is handled by dynaudnorm in the graph — no per-chunk live gain swapping.
    // Newest scheduled auto-level computation wins; an older background result is dropped.
    private int _autoLevelGen;

    // Recompute the per-track speech-run gain map. This used to run entirely on the UI thread and was the
    // app's worst stall (~800 ms, repeatedly, because a waveform arrival re-triggers it): it walked every
    // waveform bucket of every clip to find speech runs, then called a per-run peak scan that ITSELF looped
    // over every clip — O(runs × clips). Now the peak is accumulated during the same single pass, and the
    // whole computation runs on a background thread off a snapshot, posting the finished map back.
    private void RecomputeAutoLevel()
    {
        var n = _audioIds.Length;
        if (n == 0) return;
        NoteHeavyOp("RecomputeAutoLevel(snapshot)"); var alT0 = Environment.TickCount64; // the scan itself is off-thread now; this is just the copy
        EnsureAutoLevelArrays(n);
        var gen = ++_autoLevelGen;

        // Snapshot everything the computation reads (UI thread). The per-clip/track float[] peak arrays are
        // never mutated in place once published — the extractor swaps in a whole new array — so sharing the
        // references with a background thread is safe.
        var starts = (double[])_clipStarts.Clone();
        float[]?[]?[]? waves = null;
        if (_clipWaveforms != null)
        {
            waves = new float[_clipWaveforms.Length][][];
            for (var c = 0; c < _clipWaveforms.Length; c++)
                waves[c] = _clipWaveforms[c] is { } cell ? (float[]?[])cell.Clone() : null;
        }
        var on = new bool[n];
        var levels = new double[n];
        var pauses = new double[n];
        for (var t = 0; t < n; t++)
        {
            on[t] = t < _autoLevelTrack.Length && _autoLevelTrack[t];
            levels[t] = t < _trackLevel.Length ? _trackLevel[t] : DefaultLevel;
            pauses[t] = t < _trackPause.Length ? _trackPause[t] : DefaultPauseSec;
        }
        var target = _autoLevelTarget;
        DiagnosticsLogger.NoteUiOp("RecomputeAutoLevel.snapshot", alT0, Environment.TickCount64 - alT0);

        _ = Task.Run(() =>
        {
            var result = new List<(double, double, double)>[n];
            for (var t = 0; t < n; t++)
            {
                var list = new List<(double, double, double)>();
                var runs = ComputeSpeechRuns(waves, starts, t, levels[t], pauses[t]);
                if (runs != null)
                {
                    foreach (var (rs, re, peak) in runs)
                    {
                        var gain = on[t] && peak > 0.001
                            ? Math.Clamp(target / peak, target, AutoMaxGain)
                            : 1.0;
                        list.Add((rs, re, gain));
                    }
                }
                result[t] = list;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _autoLevelGen) return;      // a newer recompute superseded this one
                if (_audioIds.Length != n) return;     // footage/audio reloaded underneath us → result is stale
                var swAL = System.Diagnostics.Stopwatch.StartNew();
                _autoChunks = result;
                for (var t = 0; t < n && t < result.Length; t++)
                    Timeline.SetAutoLevelChunks(t, result[t].ToArray(), on[t]);
                ApplyAudioGains(); // rebuild once so dynaudnorm reflects the current toggles/target
                DiagnosticsLogger.NoteUiOp($"AutoLevel.apply({result.Sum(l => l.Count)} chunks)", Environment.TickCount64 - swAL.ElapsedMilliseconds, swAL.ElapsedMilliseconds);
            });
        });
    }

    // Single pass over a track's waveform: detect silence-bounded speech runs AND each run's peak at the same
    // time (the peak used to need a separate per-run scan across all clips). Pure + static so it can run off
    // the UI thread against a snapshot. Returns null when the track has no level data yet.
    private static List<(double Start, double End, double Peak)>? ComputeSpeechRuns(
        float[]?[]?[]? waves, double[] starts, int trackIdx, double threshold, double pauseSec)
    {
        if (waves == null) return null;
        const double bucketDur = 1.0 / WaveBucketsPerSec;
        var runs = new List<(double, double, double)>();
        bool inSpeech = false, anyAudio = false;
        double speechStart = 0, lastSpeechEnd = 0, silentFor = 0, peak = 0;

        for (var c = 0; c < waves.Length; c++)
        {
            var cell = waves[c];
            var peaks = (cell != null && trackIdx < cell.Length) ? cell[trackIdx] : null;
            var clipStart = c < starts.Length ? starts[c] : 0;

            if (peaks == null)
            {
                // No level data for this clip — close any open run at the clip boundary rather than
                // bridging across an un-analyzed stretch.
                if (inSpeech) { runs.Add((speechStart, lastSpeechEnd, peak)); inSpeech = false; silentFor = 0; peak = 0; }
                continue;
            }
            anyAudio = true;
            for (var b = 0; b < peaks.Length; b++)
            {
                var v = peaks[b];
                var tAbs = clipStart + b * bucketDur;
                if (v >= threshold)
                {
                    if (!inSpeech) { inSpeech = true; speechStart = tAbs; peak = 0; }
                    if (v > peak) peak = v;
                    lastSpeechEnd = tAbs + bucketDur;
                    silentFor = 0;
                }
                else if (inSpeech)
                {
                    if (v > peak) peak = v; // trailing quiet buckets are still part of the run
                    silentFor += bucketDur;
                    if (silentFor >= pauseSec)
                    {
                        runs.Add((speechStart, lastSpeechEnd, peak));
                        inSpeech = false;
                        silentFor = 0;
                        peak = 0;
                    }
                }
            }
        }
        if (inSpeech) runs.Add((speechStart, lastSpeechEnd, peak));
        return anyAudio ? runs : null;
    }

    private void BuildTrackHeaders(IReadOnlyList<string> titles)
    {
        TrackHeaders.Children.Clear();

        // status strip (aligns with the timeline ruler)
        _audioStatus = new TextBlock
        {
            Height = TimelineControl.RulerHeight, Text = "…",
            Foreground = Brush.Parse("#8CE6A0"), FontSize = 11,
            Margin = new Avalonia.Thickness(8, 4, 0, 0),
        };
        TrackHeaders.Children.Add(_audioStatus);

        // video lane header
        TrackHeaders.Children.Add(new Border
        {
            Height = TimelineControl.VideoLaneHeight,
            BorderBrush = Brush.Parse("#26262F"), BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            Child = new TextBlock
            {
                Text = "Video", Foreground = Brush.Parse("#BFD2F2"), FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(8, 6, 0, 0),
            },
        });

        // one header per audio track
        _autoLevelButtons = new ToggleButton[_audioIds.Length];
        for (var i = 0; i < _audioIds.Length; i++)
            TrackHeaders.Children.Add(BuildAudioHeader(i, titles[i]));
    }

    private Border BuildAudioHeader(int idx, string title)
    {
        var name = new TextBlock
        {
            Text = title, Foreground = Brush.Parse("#D8D8E2"), FontSize = 12,
            Margin = new Avalonia.Thickness(8, 5, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var pct = new TextBlock
        {
            // Seed from the restored per-track volume — the slider's change handler isn't attached yet,
            // so the initial Value assignment below doesn't update this label on load.
            Text = $"{(int)Math.Round(_volumes[idx] * 100)}%", Foreground = Brush.Parse("#9A9AA8"), FontSize = 11,
            FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center,
        };
        var mute = new ToggleButton { Content = "M", Width = 24, Height = 22, FontSize = 11, Padding = new Avalonia.Thickness(0), Focusable = false };
        var solo = new ToggleButton { Content = "S", Width = 24, Height = 22, FontSize = 11, Padding = new Avalonia.Thickness(0), Focusable = false };
        var auto = new ToggleButton { Content = "A", Width = 24, Height = 22, FontSize = 11, Padding = new Avalonia.Thickness(0), Focusable = false };
        mute.Classes.Add("trackToggle"); mute.Classes.Add("mute");
        solo.Classes.Add("trackToggle"); solo.Classes.Add("solo");
        auto.Classes.Add("trackToggle"); auto.Classes.Add("autolvl");
        var reset = new Button { Content = "⟳", Width = 24, Height = 22, FontSize = 12, Padding = new Avalonia.Thickness(0), Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        var seg = new ToggleButton { Content = "⇥", Width = 24, Height = 22, FontSize = 11, Padding = new Avalonia.Thickness(0), Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(mute, "Mute");
        ToolTip.SetTip(solo, "Solo");
        ToolTip.SetTip(auto, "Auto-level — lift quiet/mumbled parts of this track toward the toolbar 'Max' target");
        ToolTip.SetTip(reset, "Reset volume to 100%");
        ToolTip.SetTip(seg, "Include this track when Ctrl+← / Ctrl+→ skip to the next audio segment");
        auto.IsChecked = idx < _autoLevelTrack.Length && _autoLevelTrack[idx];
        seg.IsChecked = idx < _segSkipTrack.Length && _segSkipTrack[idx];
        if (idx < _autoLevelButtons.Length) _autoLevelButtons[idx] = auto;

        // name + volume % on the top line so the button row has room for all the toggles.
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(name, 0);
        pct.Margin = new Avalonia.Thickness(0, 5, 8, 0);
        Grid.SetColumn(pct, 1);
        nameRow.Children.Add(name);
        nameRow.Children.Add(pct);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Margin = new Avalonia.Thickness(8, 3, 0, 0),
            Children = { mute, solo, auto, reset, seg },
        };
        var left = new StackPanel { Children = { nameRow, buttons } };

        // 0–200 %, centre = 100 %. Lets you boost a quiet track up to 2× as well as cut it.
        // Left-aligned with a wide right gap so the knob stays clear of the timeline lanes.
        var fader = new Slider
        {
            Orientation = Orientation.Vertical, Minimum = 0, Maximum = 200, Value = _volumes[idx] * 100,
            Width = 20, Margin = new Avalonia.Thickness(2, 4, 0, 4), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            [ToolTip.TipProperty] = "Track volume (100 % = unchanged; up to 200 % to boost a quiet track).",
        };

        fader.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            _volumes[idx] = fader.Value / 100.0;
            pct.Text = $"{(int)Math.Round(fader.Value)}%";
            ApplyAudioGains();
            MarkDirty(contentChanged: false); // persist volume without flagging "unsaved"
        };
        mute.Click += (_, _) => { _muted[idx] = mute.IsChecked == true; ApplyAudioGains(); };
        solo.Click += (_, _) =>
        {
            _solo[idx] = solo.IsChecked == true;
            // Soloing a muted track implies you want to hear it — unmute it.
            if (_solo[idx] && _muted[idx]) { _muted[idx] = false; mute.IsChecked = false; }
            ApplyAudioGains();
        };
        auto.Click += (_, _) =>
        {
            EnsureAutoLevelArrays(_audioIds.Length);
            _autoLevelTrack[idx] = auto.IsChecked == true;
            RecomputeAutoLevel();   // (re)builds this track's chunks + refreshes the live gain
        };
        reset.Click += (_, _) => { fader.Value = 100; }; // handler resets _volumes + applies
        seg.Click += (_, _) =>
        {
            if (idx < _segSkipTrack.Length) _segSkipTrack[idx] = seg.IsChecked == true;
            _settings.SegSkipTracks = (bool[])_segSkipTrack.Clone(); // persist between sessions
            _settings.Save();
        };

        // Wide fader column + left-aligned knob = a generous gap before the timeline lanes start.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,48") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(fader, 1);
        grid.Children.Add(left);
        grid.Children.Add(fader);

        return new Border
        {
            Height = TimelineControl.AudioLaneHeight,
            BorderBrush = Brush.Parse("#26262F"), BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    // keepTimelineHeight: for a SEAMLESS clip edit the track count is unchanged, so don't collapse the
    // timeline's audio lanes to 0 height (SetTracks(0)) — that made the whole timeline visibly shrink then
    // re-expand ~120 ms later when TryConfigureAudio re-set the same count. TryConfigureAudio will re-set the
    // (same) count, leaving the height untouched.
    private void ResetAudio(bool keepTimelineHeight = false)
    {
        _audioConfigured = false;
        _audioIds = Array.Empty<int>();
        _volumes = Array.Empty<double>();
        _muted = Array.Empty<bool>();
        _solo = Array.Empty<bool>();
        if (!keepTimelineHeight) { TrackHeaders.Children.Clear(); Timeline.SetTracks(0); }
        _audioStatus = null;
        try { _mpv?.SetPropertyString("lavfi-complex", ""); } catch { /* ignore */ }
    }

    // ---- Transport ---------------------------------------------------------

    private void TogglePause()
    {
        if (_mpv == null) return;
        StopViewportRestore(); // starting playback counts as taking over the view
        try { _mpv.Pause.Set(!(MpvFlag("pause") ?? false)); }
        catch { /* unavailable before load */ }
    }

    private void SeekTo(double seconds)
    {
        if (_mpv == null || _duration <= 0) return;
        var clamped = Math.Clamp(seconds, 0, _duration);
        _mpv.RunCommandString($"seek {clamped.ToString("0.###", CultureInfo.InvariantCulture)} absolute exact");
        // Re-anchor the smooth playhead immediately so the seek lands without a poll's worth of lag (the
        // interpolator would otherwise keep predicting from the old anchor for up to one 120 ms tick).
        _phAnchorPos = clamped;
        _phAnchorWall = Environment.TickCount64;
        _smoothPos = clamped;
        _lastPlayhead = clamped; // so a paused seek recenters the log playhead on the next tick (not stale)
    }

    // Ctrl+← / Ctrl+→ jump to the previous/next speech segment start, across the tracks whose ⇥ toggle
    // is on (uses the same silence-run detection as the transcript).
    private void SkipToAudioSegment(int dir)
    {
        if (_duration <= 0 || _audioIds.Length == 0) return;
        StopViewportRestore();
        var pos = _lastPlayhead;
        var starts = new List<double>();
        for (var t = 0; t < _audioIds.Length; t++)
        {
            if (t >= _segSkipTrack.Length || !_segSkipTrack[t]) continue;
            var runs = BuildSpeechRunsFromAudio(t);
            if (runs == null) continue;
            foreach (var (s, _) in runs) starts.Add(s);
        }
        if (starts.Count == 0) return;
        starts.Sort();
        double? target = null;
        if (dir > 0) { foreach (var s in starts) if (s > pos + 0.15) { target = s; break; } }
        else { for (var i = starts.Count - 1; i >= 0; i--) if (starts[i] < pos - 0.15) { target = starts[i]; break; } }
        if (target is double tt) { SeekTo(tt); PosText.Text = Fmt(tt); }
    }

    // "M" drops a temporary bookmark at the playhead; pressing M again jumps back to it and clears it.
    // A one-shot "go do something, then return here" marker — separate from the persistent log markers.
    private void ToggleTempMarker()
    {
        if (_tempMarker is double tm)
        {
            SeekTo(tm);
            PosText.Text = Fmt(tm);
            _tempMarker = null;
            Timeline.SetTempMarker(null);
            try { _mpv?.RunCommandString("show-text \"↩ Back to temp marker\" 700"); } catch { /* ignore */ }
        }
        else
        {
            var t = CurrentTime();
            _tempMarker = t;
            Timeline.SetTempMarker(t);
            try { _mpv?.RunCommandString("show-text \"⚑ Temp marker set — press M to return\" 900"); } catch { /* ignore */ }
        }
    }

    // Shift+M: remove the temp marker (without jumping to it like plain M does).
    private void ClearTempMarker()
    {
        if (_tempMarker is null) return;
        _tempMarker = null;
        Timeline.SetTempMarker(null);
        try { _mpv?.RunCommandString("show-text \"⚑ Temp marker removed\" 700"); } catch { /* ignore */ }
    }

    // L: toggle locking the video timeline view to the playhead (centre-lock), same as the 🎯 button.
    private void ToggleViewLock()
    {
        var on = !(VideoCenterLockBtn.IsChecked == true);
        VideoCenterLockBtn.IsChecked = on;
        Timeline.LockToCenter = on;
        try { _mpv?.RunCommandString($"show-text \"{(on ? "🎯 View locked to playhead" : "View lock off")}\" 700"); } catch { /* ignore */ }
    }

    // J: jump to the most recently CREATED log entry (seek there + bring it into view).
    private void JumpToMostRecentLog()
    {
        if (_entries.Count == 0) return;
        var latest = _entries.Aggregate((a, b) => b.CreatedAtUtc >= a.CreatedAtUtc ? b : a);
        SeekTo(latest.T);
        PosText.Text = Fmt(latest.T);
        foreach (var g in _groups) if (g.Entries.Contains(latest)) { g.IsExpanded = true; break; }
        ScrollEntryIntoCenter(latest);
        try { _mpv?.RunCommandString("show-text \"↧ Most recent log\" 600"); } catch { /* ignore */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    // Show the "CAPS: ON" line above the log box only while Caps Lock is on (VK_CAPITAL toggle bit).
    // Windows-only: there is no portable "is caps lock on" query, and P/Invoking user32 off Windows
    // throws DllNotFoundException. Elsewhere we just keep the indicator hidden.
    private void UpdateCapsIndicator()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (CapsIndicator.IsVisible) CapsIndicator.IsVisible = false;
            return;
        }
        var on = (GetKeyState(0x14) & 1) != 0;
        if (CapsIndicator.IsVisible != on) CapsIndicator.IsVisible = on;
    }

    // ---- Loading -----------------------------------------------------------

    private async System.Threading.Tasks.Task OpenFootageAsync()
    {
        var top = GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select footage clips (concatenated in filename order)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Video") { Patterns = VideoExt },
                Avalonia.Platform.Storage.FilePickerFileTypes.All,
            },
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0) return;
        await ImportFootageAsync(paths);
    }

    private async System.Threading.Tasks.Task OpenFolderAsync()
    {
        var top = GetTopLevel(this);
        if (top == null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Select a folder of footage clips",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        // Recurse: a session is often filed as one folder per recording rather than a flat pile of
        // clips. Off the UI thread — a deep tree on a network volume can take a noticeable moment.
        // IgnoreInaccessible skips folders we lack permission for instead of throwing away the whole
        // scan, and the default AttributesToSkip keeps hidden/system directories out.
        var chosen = await System.Threading.Tasks.Task.Run(() =>
        {
            var exts = new HashSet<string>(VideoExt.Select(e => e.TrimStart('*')), StringComparer.OrdinalIgnoreCase);
            var all = Directory.EnumerateFiles(path, "*", new EnumerationOptions
                      {
                          RecurseSubdirectories = true,
                          IgnoreInaccessible = true,
                      })
                      .Where(f => exts.Contains(Path.GetExtension(f)));
            return PreferByStem(all).ToList();
        });

        if (chosen.Count == 0) { ClipInfo.Text = "no video files in that folder or its subfolders"; return; }

        // One divider per immediate subfolder, anchored to the first clip that folder contributes.
        // "First clip" rather than "every boundary": if a folder's clips don't end up contiguous
        // (filenames without a date prefix), that yields one marker in an odd spot instead of a
        // stutter of duplicates.
        var newMarkers = new List<DayMarker>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in chosen)
        {
            var day = ImmediateSubfolder(path, f);
            if (day == null || !seenFolders.Add(day)) continue;
            newMarkers.Add(new DayMarker { SourcePath = f, Text = day });
        }

        await ImportFootageAsync(chosen);

        if (newMarkers.Count > 0)
        {
            var known = new HashSet<string>(_dayMarkers.Select(m => m.SourcePath), StringComparer.OrdinalIgnoreCase);
            _dayMarkers.AddRange(newMarkers.Where(m => known.Add(m.SourcePath)));
            UpdateTimelineMarkers();
            MarkDirty();
        }
    }

    /// <summary>
    /// The name of the folder directly beneath <paramref name="root"/> that contains
    /// <paramref name="file"/>, or null when the file sits in the root itself.
    /// </summary>
    private static string? ImmediateSubfolder(string root, string file)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(dir)) return null;
            if (string.Equals(dir, full, StringComparison.OrdinalIgnoreCase)) return null;
            if (!dir.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;

            var rel = dir.Substring(full.Length + 1);
            var cut = rel.IndexOf(Path.DirectorySeparatorChar);
            return cut < 0 ? rel : rel.Substring(0, cut);
        }
        catch { return null; }
    }

    // When a clip exists as both .mkv and .mp4, keep the .mkv (cleaner multi-track audio + lossless
    // concat). Falls back through the extension priority list otherwise.
    // Keys on directory + stem, not stem alone: the point is to pick ONE container per recording when
    // the same clip sits alongside itself as .mkv and .mp4. Grouping by stem across a whole tree would
    // instead treat Session1/recording.mkv and Session2/recording.mkv as duplicates and silently drop
    // one of them — same-named files in sibling folders are normal for per-session recording setups.
    private static IEnumerable<string> PreferByStem(IEnumerable<string> files)
        => files
            .GroupBy(f => Path.Combine(Path.GetDirectoryName(f) ?? string.Empty,
                                       Path.GetFileNameWithoutExtension(f)),
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f =>
            {
                var i = Array.IndexOf(VideoExt, "*" + Path.GetExtension(f).ToLowerInvariant());
                return i < 0 ? int.MaxValue : i;
            }).First())
            // Ordered as if every clip sat in one folder: recordings are named with their date and time,
            // so the filename alone is the true chronological key. Sorting by full path instead would
            // interleave nothing but would rank folders lexically ("Day 10" before "Day 2").
            .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance);

    // Remove the right-click/marquee-selected clips from the current sequence. Rebuilds the EDL
    // from the remaining sources and reloads, so playback continues without the removed clips.
    private void DeleteSelectedClips()
    {
        if (_clipReloadInFlight) return; // don't edit the sequence while a reload is pending
        if (_currentSources == null || _currentSources.Count == 0) return;
        var sel = Timeline.SelectedClips.ToHashSet();
        if (sel.Count == 0) return;
        var kept = new List<string>(_currentSources.Count);
        for (var i = 0; i < _currentSources.Count; i++)
            if (!sel.Contains(i)) kept.Add(_currentSources[i]);
        Timeline.ClearSelection();
        if (kept.Count == 0)
        {
            // Removing all clips empties the footage but KEEPS the project + its save location (M18
            // invariant: a created project always has one). Re-assert the path after the full unload.
            var keepPath = _currentProjectPath;
            ResetProjectState(unloadMedia: true);
            if (!string.IsNullOrEmpty(keepPath))
            {
                _currentProjectPath = keepPath;
                SetProjectTitle(Path.GetFileName(keepPath));
                MarkDirty();
            }
            return;
        }
        // oldToNew: surviving clips → their new index; deleted clips → -1 (their logs drop, the rest follow).
        var oldToNew = new int[_currentSources.Count];
        var np = 0;
        for (var i = 0; i < _currentSources.Count; i++) oldToNew[i] = sel.Contains(i) ? -1 : np++;
        BeginSeamlessClipEdit(kept, oldToNew, sel.Count == 1 ? "delete clip" : $"delete {sel.Count} clips");
        MarkDirty();
    }

    // keepProjectMetadata=true preserves _entries / _pendingProjectAssets etc. (used by
    // OpenProjectAsync which has ALREADY populated them with the new project's contents). For
    // everything else (Open footage / Open folder / Demo), we fully reset so old logs don't bleed.
    private async void LoadTimeline(IReadOnlyList<string> sources, bool keepProjectMetadata = false,
        bool seamlessEdit = false, double?[]? knownDurations = null)
    {
        if (_mpv == null) { _afterClipsBuilt = null; return; }
        _clipReloadInFlight = true; // cleared in BuildTimelineClipsAsync's Post (success) or the catch (failure)
        // A seamless clip edit (reorder / insert / delete / chronological sort) must not feel like a reload:
        // keep the user's play/pause + speed across the EDL swap instead of force-unpausing and snapping to 1×.
        var wasPaused = false;
        var savedSpeed = _speed;
        if (seamlessEdit) { try { wasPaused = MpvFlag("pause") ?? false; } catch { /* not yet loaded */ } }
        try
        {
            if (!keepProjectMetadata)
                ResetProjectState(unloadMedia: false);
            else
            {
                // Even when keeping metadata, drop the previous footage's audio + asset mirrors.
                _extractCts?.Cancel();
                _extractCts = new CancellationTokenSource();
                _extractTotal = 0;
                _extractDone = 0;
                UpdateProgressUi();

                _transcribeCts?.Cancel();
                _transcribePause.Set();
                _transcribePaused = false;
                PauseTranscriptBtn.Content = "⏸";
                TranscribeControls.IsVisible = false;
                foreach (var col in _trackSegments) col.Clear();
                foreach (var raw in _rawSegmentsByTrack) raw.Clear();
                for (var i = 0; i < _activeSegmentPerTrack.Length; i++) _activeSegmentPerTrack[i] = null;
                _clipThumbnailJpegs = null;
                _clipThumbnailTimes = null;
                _clipWaveforms = null;
                _cachedClipAssets = null; _assetsDirty = true;
                TranscriptProgress.IsVisible = false;
                ResetAudio(keepTimelineHeight: seamlessEdit); // don't collapse the timeline height on a reorder
            }

            // Blank the timeline only on a FRESH load. A seamless edit keeps the old clips painted until the
            // new (synchronously-known) layout arrives — no half-second "timeline vanished" gap.
            if (!seamlessEdit) Timeline.SetClips(Array.Empty<TimelineClip>(), 0);
            _currentSources = sources;

            var edl = BuildEdl(sources);
            try
            {
                // macOS: issue loadfile synchronously. The binding's InvokeAsync waits for a completion
                // reply from the mpv event loop, which never arrives here — the await never returns and
                // Tick() (playhead, duration, audio-track config) never runs again.
                if (OperatingSystem.IsMacOS() && Util.MacNative.CanRunCommands)
                    Util.MacNative.Command(MpvHandle(_mpv), "loadfile", edl, "replace");
                else
                    await _mpv.LoadFile(edl).InvokeAsync();
            }
            catch (Exception ex) { DiagnosticsLogger.LogException("mpv LoadFile", ex); throw; }
            if (seamlessEdit)
            {
                _mpv.Pause.Set(wasPaused);    // keep play/pause exactly as it was
                SetPlaybackSpeed(savedSpeed); // keep the user's speed (don't snap to 1×)
            }
            else
            {
                _mpv.Pause.Set(false);
                SpeedBox.SelectedIndex = Array.IndexOf(Speeds, 1.0);
            }

            ClipInfo.Text = sources.Count == 1
                ? "1 clip loaded"
                : $"{sources.Count} clips loaded — one continuous timeline";

            _ = BuildTimelineClipsAsync(sources, knownDurations, seamlessEdit);
        }
        catch (Exception ex)
        {
            ClipInfo.Text = "load failed: " + ex.Message;
            // The rebuild never ran → drop the pending remap hook (so it can't misfire on a later reload)
            // and release the in-flight guard.
            _afterClipsBuilt = null;
            _clipReloadInFlight = false;
        }
    }

    // Probe each source's duration (off the UI thread) to lay out the clip rectangles. knownDurations lets a
    // clip EDIT skip the probe for clips whose duration is already known (reorder/delete know all of them;
    // insert/append know all but the new files) — this is what kills the bulk-re-probe lag on every edit.
    private async System.Threading.Tasks.Task BuildTimelineClipsAsync(IReadOnlyList<string> sources,
        double?[]? knownDurations = null, bool seamlessEdit = false)
    {
        var probe = ToolPath("ffprobe.exe");
        var (clips, total, unreadable) = await System.Threading.Tasks.Task.Run(() =>
        {
            var list = new List<TimelineClip>();
            var acc = 0.0;
            var bad = new List<string>();
            for (var si = 0; si < sources.Count; si++)
            {
                var s = sources[si];
                var known = knownDurations != null && si < knownDurations.Length ? knownDurations[si] : null;
                var local = !s.StartsWith("av://") && probe != null && File.Exists(s);
                // Use the known duration when we have it; only probe the genuinely-new files.
                var dur = known is double kd && kd > 0 ? kd
                        : local ? ProbeDuration(probe!, s) : 0; // probe non-null when local (flow analysis can't see it)
                // A local file that exists but probes to 0 is unreadable by ffprobe/ffmpeg — almost always
                // an mp4 with no moov atom (still being written / crashed / interrupted). Its thumbnails
                // AND waveforms will silently come back empty, so flag it for a visible warning.
                if (local && dur <= 0) bad.Add(Path.GetFileName(s));
                list.Add(new TimelineClip
                {
                    Name = Path.GetFileNameWithoutExtension(s),
                    SourcePath = s,
                    Start = acc,
                    Duration = dur,
                });
                acc += dur;
            }
            return (list.ToArray(), acc, bad);
        });

        Dispatcher.UIThread.Post(() =>
        {
            // Wrap the whole layout-apply: a fault in the edit hook (asset carry / remap) must never crash the
            // app or wedge the UI by leaving _clipReloadInFlight stuck true — log it and recover.
            try
            {
                var swPost = System.Diagnostics.Stopwatch.StartNew();
                Timeline.SetClips(clips, total, preserveView: seamlessEdit);
                var msSetClips = swPost.ElapsedMilliseconds;
                _clipStarts = clips.Select(c => c.Start).ToArray();
                _clipDurations = clips.Select(c => c.Duration).ToArray();
                _clipNames = clips.Select(c => c.Name).ToArray();
                _clipThumbnailJpegs = new byte[clips.Length][][];
                _clipThumbnailTimes = new double[clips.Length][];
                _clipWaveforms = new float[clips.Length][][];
                _cachedClipAssets = null; _assetsDirty = true;
                // A pending edit (insert / reorder) remaps logs + sync anchors to follow their clip now that the
                // new clip layout (_clipStarts) is known — must run BEFORE RebuildGroupsAndMarkers so groups +
                // markers reflect the remapped times. Cleared after one use. Isolate its failure so the group
                // rebuild ALWAYS runs (else a hook fault would leave logs un-regrouped at stale times).
                var afterBuilt = _afterClipsBuilt; _afterClipsBuilt = null;
                try { afterBuilt?.Invoke(); }
                catch (Exception hookEx) { DiagnosticsLogger.LogException("afterClipsBuilt hook", hookEx); }
                var msBeforeGroups = swPost.ElapsedMilliseconds;
                RebuildGroupsAndMarkers();
                var msGroups = swPost.ElapsedMilliseconds - msBeforeGroups;
                TryRestoreEmbeddedAssets();
                var msRestoreClipSide = swPost.ElapsedMilliseconds - msBeforeGroups - msGroups;
                HideSplash(); // the workspace is usable from here
                if (swPost.ElapsedMilliseconds > 150)
                    DiagnosticsLogger.Log($"clip layout post: setClips {msSetClips} ms, groups+markers {msGroups} ms, restore {msRestoreClipSide} ms, total {swPost.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.LogException("BuildTimelineClipsAsync.Post", ex);
                _afterClipsBuilt = null;
                try { ClipInfo.Text = "clip update hit an error (logged to diagnostics.log)"; } catch { /* UI gone */ }
            }
            finally
            {
                _clipReloadInFlight = false; // ALWAYS release so a later edit isn't permanently blocked
            }
        });

        // Warn (visibly) about files ffprobe/ffmpeg can't read — otherwise their empty thumbnails/waveforms
        // look like a silent app bug. Almost always an unfinalized mp4 (no moov atom); the .mkv is crash-safe.
        if (unreadable.Count > 0)
        {
            var names = string.Join(", ", unreadable.Take(3)).Replace("\"", "'");
            var more = unreadable.Count > 3 ? $" +{unreadable.Count - 3} more" : "";
            Dispatcher.UIThread.Post(() =>
            {
                try { _mpv?.RunCommandString(
                    $"show-text \"⚠ {unreadable.Count} file(s) unreadable ({names}{more}) — mp4 not finalized? Re-export or use the .mkv.\" 9000"); }
                catch { /* OSD is best-effort */ }
            });
        }

        // Kick off thumbnail extraction (waveforms wait until the track count is known) - but only once the
        // embedded-asset decode has landed, so clips whose filmstrip came from the project file are skipped.
        var ct = _extractCts?.Token ?? System.Threading.CancellationToken.None;
        Dispatcher.UIThread.Post(() => RunWhenAssetsDecoded(() =>
        {
            _extractKind = "thumbnails + waveforms"; // a normal load does both (regen narrows this)
            System.Threading.Interlocked.Add(ref _extractTotal, clips.Length);
            UpdateProgressUi();
            _ = System.Threading.Tasks.Task.Run(() => ExtractThumbnails(clips, ct), ct);
        }));
    }

    private void StartBackgroundExtraction(IReadOnlyList<string> sources, int trackCount)
    {
        var ct = _extractCts?.Token ?? System.Threading.CancellationToken.None;
        System.Threading.Interlocked.Add(ref _extractTotal, sources.Count * trackCount);
        Dispatcher.UIThread.Post(UpdateProgressUi);
        _ = System.Threading.Tasks.Task.Run(() => ExtractWaveforms(sources, trackCount, ct), ct);
    }

    private void UpdateProgressUi()
    {
        if (_extractTotal <= 0 || _extractDone >= _extractTotal)
        {
            ExtractProgress.IsVisible = false;
            ExtractLabel.IsVisible = false;
            return;
        }
        ExtractProgress.Maximum = _extractTotal;
        ExtractProgress.Value = _extractDone;
        ExtractProgress.IsVisible = true;
        ExtractLabel.IsVisible = true;
        ExtractLabel.Text = $"generating {_extractKind} · {_extractDone}/{_extractTotal}";
    }

    private void BumpProgress()
    {
        System.Threading.Interlocked.Increment(ref _extractDone);
        Dispatcher.UIThread.Post(UpdateProgressUi);
    }

    // DaVinci-style filmstrip: ~24 frames per clip max, interval scales with duration but is at least
    // 2s. Frames are cached per (file, interval). Posted back to the timeline as a (Bitmap[], time[])
    // pair; the control picks the nearest-by-time frame for each tile position.
    //
    // Extraction uses INPUT-SEEK ("-ss BEFORE -i") with ONE ffmpeg per frame, run in parallel up to
    // FilmstripParallelism. Input-seek jumps to the keyframe at the target time in O(1) instead of
    // decoding from the start of the file, so a 1-hour clip extracts in seconds instead of minutes.
    private const int FilmstripMaxFrames = 24;
    private const double FilmstripMinInterval = 2.0;
    // Keep concurrent ffmpegs low so we don't peg the disk/CPU while the user wants to scrub.
    // ChildProcesses also runs them at BelowNormal priority — together that keeps the UI snappy.
    private const int FilmstripParallelism = 2;

    private void ExtractThumbnails(TimelineClip[] clips, System.Threading.CancellationToken ct)
    {
        var ffmpeg = ToolPath("ffmpeg.exe");
        if (ffmpeg == null) return;
        var cacheDir = Path.Combine(CacheRoot, "thumbs");
        Directory.CreateDirectory(cacheDir);

        // Spinner on every clip's video lane until its filmstrip is in (cleared per clip below, even on failure).
        var n = clips.Length;
        Dispatcher.UIThread.Post(() => { for (var i = 0; i < n; i++) Timeline.SetThumbPending(i, true); });

        for (var i = 0; i < clips.Length; i++)
        {
            if (ct.IsCancellationRequested) return;
            // Filmstrip already restored from the project file (or carried through an edit) -> nothing to do.
            var have = _clipThumbnailJpegs;
            if (have != null && i < have.Length && have[i] != null && have[i].Length > 0)
            {
                BumpProgress();
                var done = i;
                Dispatcher.UIThread.Post(() => Timeline.SetThumbPending(done, false));
                continue;
            }
            try { ExtractOneClipThumbnails(i, clips[i], ffmpeg, cacheDir, ct); }
            catch (OperationCanceledException) { return; }
            catch { /* skip this clip's filmstrip, but keep going */ }
            BumpProgress(); // one progress unit per clip whether it succeeded or not
            var ci = i;
            Dispatcher.UIThread.Post(() => Timeline.SetThumbPending(ci, false)); // clear spinner (incl. on failure)
        }
    }

    private void ExtractOneClipThumbnails(int i, TimelineClip c, string ffmpeg, string cacheDir,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(c.SourcePath) || !File.Exists(c.SourcePath)) return;
        if (c.Duration <= 0) return;

        FileInfo fi;
        try { fi = new FileInfo(c.SourcePath); } catch { return; }

        var interval = Math.Max(FilmstripMinInterval, c.Duration / FilmstripMaxFrames);
        interval = Math.Round(interval * 100) / 100.0;
        var frameCount = Math.Max(1, (int)Math.Floor(c.Duration / interval) + 1);
        if (frameCount > FilmstripMaxFrames) frameCount = FilmstripMaxFrames;

        var stem = SafeStem(c.SourcePath);
        var prefix = $"{stem}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks:X}_int{Inv(interval)}_";

        var paths = new string[frameCount];
        var times = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            times[f] = f * interval;
            paths[f] = Path.Combine(cacheDir, $"{prefix}{f:D3}.jpg");
        }

        // Extract frames serially — input-seek per frame is fast enough that we don't need to
        // bother with a semaphore + Task.Run dance, and the serial path is impossible to
        // deadlock. Per-frame failures (NAS hiccups, codec quirks) are swallowed so one bad
        // frame doesn't abort the rest of the clip or the worker.
        for (var f = 0; f < frameCount; f++)
        {
            if (ct.IsCancellationRequested) return;
            if (File.Exists(paths[f])) continue;
            try { ExtractSingleFrame(ffmpeg, c.SourcePath!, times[f], paths[f], ct); }
            catch { /* keep going */ }
        }

        if (ct.IsCancellationRequested) return;

        // Load whatever we ended up with — partial filmstrips are fine. Also capture the raw JPG
        // bytes so we can embed them in the project file later.
        var bmps = new List<Bitmap>(frameCount);
        var keptTimes = new List<double>(frameCount);
        var keptBytes = new List<byte[]>(frameCount);
        for (var f = 0; f < frameCount; f++)
        {
            if (!File.Exists(paths[f])) continue;
            try
            {
                var bytes = File.ReadAllBytes(paths[f]);
                using var ms = new MemoryStream(bytes);
                bmps.Add(new Bitmap(ms));
                keptTimes.Add(times[f]);
                keptBytes.Add(bytes);
            }
            catch { }
        }

        if (bmps.Count > 0)
        {
            var idx = i;
            var bmpsArr = bmps.ToArray();
            var timesArr = keptTimes.ToArray();
            var bytesArr = keptBytes.ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                Timeline.SetClipThumbnails(idx, bmpsArr, timesArr);
                if (_clipThumbnailJpegs != null && idx < _clipThumbnailJpegs.Length)
                {
                    _clipThumbnailJpegs[idx] = bytesArr;
                    _clipThumbnailTimes![idx] = timesArr;
                    _assetsDirty = true; // invalidate the encoded-asset cache
                }
                MarkDirty();
            });
        }
    }

    private static void ExtractSingleFrame(string ffmpeg, string source, double t, string outPath,
        CancellationToken ct)
    {
        // -ss BEFORE -i = fast input seek (keyframe). For thumbnail-grade fidelity this is fine.
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-y", "-v", "error",
            "-ss", Inv(t),
            "-i", source,
            "-frames:v", "1",
            "-vf", "scale=160:-2",
            "-q:v", "5",
            outPath,
        }) psi.ArgumentList.Add(a);
        try
        {
            using var p = ChildProcesses.Start(psi);
            WaitForExitOrCancel(p, ct, 20000);
        }
        catch { /* swallow per-frame failures; missing frames are fine */ }
    }

    // Per-(clip, audio-stream) peak buckets at 32 buckets/sec from a mono 8 kHz downmix. Cached on
    // disk so re-opens are instant. Posted back to the timeline as float[] of normalized peaks;
    // the control draws them inside each audio-lane clip rect, scaled by the fader.
    //
    // Work is ordered by the live playhead: the clip under the cursor and clips to its RIGHT extract
    // first; clips fully to the left are done last. The order is re-evaluated before each item, so
    // seeking mid-extraction re-prioritizes around the new position.
    private void ExtractWaveforms(IReadOnlyList<string> sources, int trackCount, CancellationToken ct)
    {
        var ffmpeg = ToolPath("ffmpeg.exe");
        if (ffmpeg == null) return;
        var cacheDir = Path.Combine(CacheRoot, "waves");
        Directory.CreateDirectory(cacheDir);

        const int sampleRate = 8000;
        const int bucketsPerSec = 32;
        const int samplesPerBucket = sampleRate / bucketsPerSec; // 250

        var pending = new List<(int clip, int track)>();
        for (var c = 0; c < sources.Count; c++)
            for (var t = 0; t < trackCount; t++)
            {
                // Skip pairs already present in memory (carried across a seamless edit, or just-restored from
                // the project) so a reorder/insert doesn't redundantly re-extract unchanged waveforms.
                var have = _clipWaveforms != null && c < _clipWaveforms.Length && _clipWaveforms[c] != null
                           && t < _clipWaveforms[c]!.Length && _clipWaveforms[c]![t] != null;
                if (!have) pending.Add((c, t));
            }

        // Show a loading spinner on each pending audio segment (cleared when its waveform arrives / its worker
        // finishes, even on failure).
        var pendingSnapshot = pending.ToList();
        Dispatcher.UIThread.Post(() => { foreach (var (c, t) in pendingSnapshot) Timeline.SetWavePending(c, t, true); });

        // Snapshot the layout-array REFERENCES once: the UI thread reassigns _clipStarts/_clipDurations on a
        // reload, and reading them across threads in the loop below could tear (a .Length check passing against
        // the old array while the index hits a newly-swapped shorter one). A reload cancels this run anyway, so
        // a stable snapshot is correct for the priority calc.
        var startsSnap = _clipStarts;
        var dursSnap = _clipDurations;

        // Pull the next-best (playhead-priority) pending pair under a lock — lets several decoder workers run
        // concurrently without racing on the shared list.
        var gate = new object();
        bool TryTakeNext(out int clipIdx, out int track)
        {
            clipIdx = track = -1;
            lock (gate)
            {
                if (pending.Count == 0) return false;
                var playhead = _lastPlayhead;
                var bestIdx = 0;
                var bestKey = double.MaxValue;
                for (var i = 0; i < pending.Count; i++)
                {
                    var (c, tr) = pending[i];
                    var start = c < startsSnap.Length ? startsSnap[c] : 0;
                    var dur = c < dursSnap.Length ? dursSnap[c] : 0;
                    var end = start + dur;
                    var key = (dur > 0 && end <= playhead) ? 1e12 + (playhead - start) : Math.Max(0, start - playhead);
                    key = key * 16 + tr;
                    if (key < bestKey) { bestKey = key; bestIdx = i; }
                }
                (clipIdx, track) = pending[bestIdx];
                pending.RemoveAt(bestIdx);
                return true;
            }
        }

        void ProcessOne(int clipIdx, int track)
        {
            var src = sources[clipIdx];
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return;

            FileInfo fi;
            try { fi = new FileInfo(src); } catch { return; }

            // v2 prefix forces a one-time re-extraction so any older corrupt/format-mismatched
            // cache files are bypassed rather than silently producing empty waveforms.
            var cacheKey = $"v2_{SafeStem(src)}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks:X}_a{track}_b{bucketsPerSec}.f32";
            var cachePath = Path.Combine(cacheDir, cacheKey);

            float[]? peaks = null;
            if (File.Exists(cachePath))
            {
                try
                {
                    var bytes = File.ReadAllBytes(cachePath);
                    peaks = new float[bytes.Length / 4];
                    Buffer.BlockCopy(bytes, 0, peaks, 0, peaks.Length * 4);
                }
                catch { peaks = null; }
            }

            if (peaks == null)
            {
                peaks = ExtractPeaksFromFfmpeg(ffmpeg, src, track, sampleRate, samplesPerBucket, ct);
                if (peaks == null) return;
                try
                {
                    var bytes = new byte[peaks.Length * 4];
                    Buffer.BlockCopy(peaks, 0, bytes, 0, bytes.Length);
                    File.WriteAllBytes(cachePath, bytes);
                }
                catch { /* cache is best-effort */ }
            }

            if (ct.IsCancellationRequested) return;
            var capturedClip = clipIdx;
            var capturedTrack = track;
            var capturedPeaks = peaks;
            var capturedTrackCount = trackCount;
            Dispatcher.UIThread.Post(() =>
            {
                Timeline.SetClipWaveform(capturedClip, capturedTrack, capturedPeaks);
                if (_clipWaveforms != null && capturedClip < _clipWaveforms.Length)
                {
                    var cell = _clipWaveforms[capturedClip] ??= new float[capturedTrackCount][];
                    if (capturedTrack < cell.Length) cell[capturedTrack] = capturedPeaks;
                    _assetsDirty = true; // invalidate the encoded-asset cache
                }
                // New audio level data for this track → re-segment its transcript bubbles from
                // the actual waveform (debounced so a burst of completing clips coalesces).
                if (capturedTrack < _rawSegmentsByTrack.Count && _rawSegmentsByTrack[capturedTrack].Count > 0)
                    ScheduleTranscriptRebuild();
                MarkDirty();
            });
        }

        void Worker()
        {
            while (!ct.IsCancellationRequested && TryTakeNext(out var clipIdx, out var track))
            {
                try { ProcessOne(clipIdx, track); }
                catch (OperationCanceledException) { return; }
                catch { /* one-off failure: skip, keep going */ }
                finally
                {
                    BumpProgress();
                    var cc = clipIdx; var tt = track;
                    Dispatcher.UIThread.Post(() => Timeline.SetWavePending(cc, tt, false)); // clear spinner (incl. on failure)
                }
            }
        }

        // Decode several clips/tracks at once (pure wall-clock win — was fully serial). Capped to keep disk/NAS
        // I/O sane; each ffmpeg is its own process so this just overlaps their decode time.
        var parallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        var workers = Math.Max(1, Math.Min(parallelism, pending.Count));
        var tasks = new System.Threading.Tasks.Task[workers];
        for (var i = 0; i < workers; i++) tasks[i] = System.Threading.Tasks.Task.Run(Worker, ct);
        try { System.Threading.Tasks.Task.WaitAll(tasks); }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException) { /* cancelled */ }
    }

    private static float[]? ExtractPeaksFromFfmpeg(string ffmpeg, string path, int streamIdx,
        int sampleRate, int samplesPerBucket, System.Threading.CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "-v", "error", "-i", path,
                // Trailing "?" = optional stream map: a video-only mp4 (or one with fewer audio tracks than
                // the .mkv had) yields no audio instead of erroring the whole ffmpeg run.
                "-map", $"0:a:{streamIdx}?",
                "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "s16le", "-",
            }) psi.ArgumentList.Add(a);

            using var p = ChildProcesses.Start(psi);
            var stream = p.StandardOutput.BaseStream;
            var buf = new byte[samplesPerBucket * 2];
            var peaks = new List<float>(1024);

            while (!ct.IsCancellationRequested)
            {
                var read = ReadFully(stream, buf, 0, buf.Length);
                if (read < 2) break;
                short max = 0;
                for (var k = 0; k + 1 < read; k += 2)
                {
                    var s = (short)(buf[k] | (buf[k + 1] << 8));
                    var a = (short)(s < 0 ? -s : s);
                    if (a > max) max = a;
                }
                peaks.Add(max / 32768f);
            }
            try { if (!p.HasExited) p.Kill(true); } catch { }
            p.WaitForExit(2000);
            return peaks.ToArray();
        }
        catch { return null; }
    }

    // Like Process.WaitForExit(timeout) but polls so the user can close the app or cancel a
    // background extraction without us blocking on a long ffmpeg run. Kills the process if cancelled.
    private static bool WaitForExitOrCancel(Process p, CancellationToken ct, int maxMs)
    {
        // Teardown (Home / close / crash) runs ChildProcesses.KillAll, which disposes every tracked process
        // while a worker may still be sitting in this loop. Every Process call after that throws
        // InvalidOperationException ("No process is associated with this object") - treat it as exited.
        var deadline = maxMs == int.MaxValue ? long.MaxValue : Environment.TickCount64 + maxMs;
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                if (p.WaitForExit(250)) return true;
                if (ct.IsCancellationRequested)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }
            }
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            return p.HasExited;
        }
        catch (InvalidOperationException) { return true; }
    }

    private static int ReadFully(System.IO.Stream s, byte[] buf, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = s.Read(buf, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static string SafeStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path) ?? "clip";
        var bad = Path.GetInvalidFileNameChars();
        var chars = stem.Select(c => bad.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    // Stable across rebuilds: caches survive `dotnet build` (which wipes bin/) so reopening a project
    // is instant. Keyed by source file identity (size + mtime), so it's shared across projects too.
    private static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FootageReviewer", "cache");

    // ---- Manual log + timer offset (transcript stage 1) --------------------

    private double CurrentTime() => MpvNum("playback-time") ?? 0;

    // True when the video is actively playing (mpv not paused). Used to gate the log-playhead
    // centre-lock so it only snaps the list during playback (you can scroll freely when paused).
    private bool IsPlaying()
    {
        if (_mpv == null) return false;
        try { return !(MpvFlag("pause") ?? true); } catch { return false; }
    }

    private void OnEntryTextChanged()
    {
        var len = (EntryInput.Text ?? "").Length;
        // Stamp the moment typing STARTS, so the timestamp is when the event happened — not when you
        // finished writing it up. The empty→non-empty transition is also the first keystroke, so
        // that's where Auto-pause kicks in.
        if (len > 0 && _pendingEntryTime is null)
        {
            _pendingEntryTime = CurrentTime();
            _pendingFromTranscript = false; // typed log → eligible for the new-log offset
            if (AutoPauseBtn.IsChecked == true)
            {
                try { _mpv?.Pause.Set(true); } catch { /* unavailable before load */ }
            }
        }
        if (len == 0) { _pendingEntryTime = null; _lastCopyTrack = -1; } // box cleared → reset copy-chaining
        UpdatePendingHint();
        ScheduleSpellRecheck();
    }

    private void OnEntryInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter logs the entry; Shift+Enter inserts a newline (the box has AcceptsReturn=true, so when
        // we DON'T handle it the TextBox adds the line break itself).
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            SubmitEntry();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Back)
        {
            // Each backspace undoes the next pending auto-edit (auto-cap, then autocorrect), phone-style.
            if (TryPopAutoEdit()) { e.Handled = true; return; }
            // Backspace on an already-empty box = "never mind": resume playback and drop focus.
            if (string.IsNullOrEmpty(EntryInput.Text))
            {
                _pendingEntryTime = null;
                UpdatePendingHint();
                try { _mpv?.Pause.Set(false); } catch { /* unavailable before load */ }
                Timeline.Focus();
                e.Handled = true;
            }
            return;
        }
        // Any other keystroke invalidates the pending auto-edits (they only apply immediately after).
        _autoEdits.Clear();
    }

    // ---- Autocorrect + auto-capitalize + spell-check (logger box) -----------

    // On a word boundary (space / punctuation), autocorrect the just-finished word and capitalize it if it
    // starts a sentence. Both edits are pushed onto _autoEdits (correct first, cap last) so backspaces undo
    // them in reverse — auto-cap, then autocorrect, then a normal delete.
    private void OnEntryTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 } s) return;
        var ch = s[0];
        if (!(ch == ' ' || ch == '\t' || ch == '.' || ch == ',' || ch == '!' ||
              ch == '?' || ch == ';' || ch == ':')) return;
        if (!_settings.AutoCorrectEnabled && !_settings.AutoCapitalizeEnabled) return;

        var text = EntryInput.Text ?? "";
        var caret = Math.Clamp(EntryInput.CaretIndex, 0, text.Length);
        if (EntryInput.SelectionStart != EntryInput.SelectionEnd) return; // skip when a selection is active
        var start = caret;
        while (start > 0 && AutoCorrect.IsWordChar(text[start - 1])) start--;
        if (start >= caret) return; // no token before the boundary
        var token = text.Substring(start, caret - start);

        // Stage 1: autocorrect.
        var word1 = token;
        if (_settings.AutoCorrectEnabled && AutoCorrect.TryCorrect(token, out var fix)) word1 = fix;

        // Stage 2: capitalize the first letter if the word starts a sentence.
        var word2 = word1;
        if (_settings.AutoCapitalizeEnabled && word1.Length > 0 && char.IsLower(word1[0]) &&
            AutoCorrect.IsSentenceStart(text, start))
            word2 = char.ToUpperInvariant(word1[0]) + word1[1..];

        if (string.Equals(word2, token, StringComparison.Ordinal)) return; // nothing changed → insert normally

        EntryInput.Text = text[..start] + word2 + s + text[caret..];
        EntryInput.CaretIndex = start + word2.Length + 1;

        _autoEdits.Clear();
        if (!string.Equals(word1, token, StringComparison.Ordinal))
            _autoEdits.Add((start, token, word1));   // undo autocorrect (popped 2nd)
        if (!string.Equals(word2, word1, StringComparison.Ordinal))
            _autoEdits.Add((start, word1, word2));    // undo capitalization (popped 1st)
        e.Handled = true; // we inserted the boundary char as part of the new text
    }

    // Revert the most recent pending auto-edit if the text still matches. Returns true if it reverted.
    private bool TryPopAutoEdit()
    {
        if (_autoEdits.Count == 0) return false;
        var ed = _autoEdits[^1];
        var text = EntryInput.Text ?? "";
        var end = ed.start + ed.after.Length;
        // Expect: [after][boundary] at ed.start, caret just past the boundary.
        if (end + 1 > text.Length || EntryInput.CaretIndex != end + 1 ||
            !string.Equals(text.Substring(ed.start, ed.after.Length), ed.after, StringComparison.Ordinal))
        {
            _autoEdits.Clear();
            return false;
        }
        _autoEdits.RemoveAt(_autoEdits.Count - 1);
        EntryInput.Text = text[..ed.start] + ed.before + text[end..];
        EntryInput.CaretIndex = ed.start + ed.before.Length + 1;
        return true;
    }

    // (Re)load or clear the spell-checker according to the current settings.
    private void ApplySpellSettings()
    {
        _spell.SetAllowlist(_settings.SpellAllowlist);
        if (_settings.SpellCheckEnabled)
        {
            if (_spell.IsReady) RecheckSpelling();
            else _spell.BeginLoad();
        }
        else
        {
            _misspelledSpans = Array.Empty<(int, int)>();
            SpellAdorner.SetSpans(_misspelledSpans);
        }
    }

    private void ScheduleSpellRecheck()
    {
        if (!_settings.SpellCheckEnabled || !_spell.IsReady || _spellDebounce == null) return;
        _spellDebounce.Stop();
        _spellDebounce.Start();
    }

    private void RecheckSpelling()
    {
        if (!_settings.SpellCheckEnabled || !_spell.IsReady)
        {
            _misspelledSpans = Array.Empty<(int, int)>();
            SpellAdorner.SetSpans(_misspelledSpans);
            return;
        }
        // Run the per-word Hunspell pass off the UI thread (the log box has no length cap — pasted
        // transcript bubbles can make it large). Apply the result only if the text hasn't changed since.
        var text = EntryInput.Text ?? "";
        Task.Run(() =>
        {
            var list = new List<(int, int)>();
            foreach (var (start, len) in WordSpans(text))
            {
                var w = text.Substring(start, len);
                if (SkipForSpell(w)) continue;
                if (!_spell.Check(w)) list.Add((start, len));
            }
            var arr = list.ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                if ((EntryInput.Text ?? "") != text) return; // superseded by a newer edit
                _misspelledSpans = arr;
                SpellAdorner.SetSpans(_misspelledSpans);
            });
        });
    }

    // Maximal runs of letters/apostrophes, as (start, length) spans.
    private static IEnumerable<(int start, int len)> WordSpans(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (!AutoCorrect.IsWordChar(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && AutoCorrect.IsWordChar(text[i])) i++;
            yield return (start, i - start);
        }
    }

    // Don't spell-flag: short tokens, anything with a digit, @mentions, acronyms/usernames (internal caps),
    // or tokens that are just apostrophes.
    private static bool SkipForSpell(string w)
    {
        if (w.Length < 2) return true;
        if (w[0] == '\'' || w[0] == '@') return true;
        for (int i = 0; i < w.Length; i++)
        {
            var c = w[i];
            if (char.IsDigit(c)) return true;
            if (i > 0 && char.IsUpper(c)) return true; // CamelCase / ALLCAPS / usernames
        }
        return false;
    }

    // Right-click (Tunnel, so it runs before the TextBox's built-in flyout) on a misspelled word →
    // suggestions menu. We resolve the word UNDER THE CURSOR on demand (not from the cached span list,
    // which can lag the squiggle) and only suppress the default menu when the word is actually misspelled.
    private void OnEntryContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!_settings.SpellCheckEnabled || !_spell.IsReady) return;
        if (!e.TryGetPosition(SpellAdorner, out var pos)) return; // keyboard menu key → let default show
        if (SpellAdorner.CharIndexAt(pos) is not { } ci) return;
        var text = EntryInput.Text ?? "";
        if (text.Length == 0) return;
        // The hit-test clamps clicks in empty space to the nearest text index; require the click to land
        // ON a word character so right-clicking past/below the text shows the normal cut/copy/paste menu.
        if (ci < 0 || ci >= text.Length || !AutoCorrect.IsWordChar(text[ci])) return;

        // Expand the click index to the word around it.
        var start = Math.Clamp(ci, 0, text.Length);
        while (start > 0 && AutoCorrect.IsWordChar(text[start - 1])) start--;
        var end = Math.Clamp(ci, 0, text.Length);
        while (end < text.Length && AutoCorrect.IsWordChar(text[end])) end++;
        if (end <= start) return;
        var word = text.Substring(start, end - start);
        if (SkipForSpell(word) || _spell.Check(word)) return; // correctly-spelled / skipped → default menu

        var span = (start, end - start);
        var menu = new ContextMenu();
        var sugs = _spell.Suggest(word);
        if (sugs.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
        foreach (var sug in sugs)
        {
            var replacement = sug;
            var mi = new MenuItem { Header = replacement, FontWeight = FontWeight.SemiBold };
            mi.Click += (_, _) => ReplaceMisspelledSpan(span, replacement);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var addItem = new MenuItem { Header = "Add to dictionary" };
        addItem.Click += (_, _) =>
        {
            _spell.AddToAllowlist(word);
            _settings.SpellAllowlist = _spell.Allowlist.ToList();
            _settings.Save();
            RecheckSpelling();
        };
        menu.Items.Add(addItem);
        menu.Open(EntryInput);
        e.Handled = true; // suppress the default cut/copy/paste flyout when over a misspelling
    }

    private void ReplaceMisspelledSpan((int start, int len) span, string replacement)
    {
        var text = EntryInput.Text ?? "";
        if (span.start < 0 || span.start + span.len > text.Length) return;
        _autoEdits.Clear(); // a programmatic edit invalidates any pending auto-edit undo
        EntryInput.Text = text[..span.start] + replacement + text[(span.start + span.len)..];
        EntryInput.CaretIndex = span.start + replacement.Length;
        EntryInput.Focus();
        RecheckSpelling();
    }

    // Hotkey (Ctrl+1/2/3 → tracks 1/2/3): copy the most-recent bubble at/before the playhead on a track
    // into the log, exactly as clicking that bubble's copy button would.
    private void CopyLastBubble(int track)
    {
        var seg = TranscriptTimeline.LastBubbleAt(track, CurrentTime());
        if (seg != null) CopyBubbleToLogger(track, seg);
    }

    // Ctrl+Shift+1/2/3 — log the track's last bubble straight away (no stop in the text box). Whatever the
    // user had already typed is preserved: the bubble is committed as its own entry and the draft is restored.
    private void LogLastBubbleImmediately(int track)
    {
        var seg = TranscriptTimeline.LastBubbleAt(track, CurrentTime());
        if (seg == null) { ClipInfo.Text = "no transcript bubble on that track yet"; return; }

        var draft = EntryInput.Text ?? "";
        var draftPending = _pendingEntryTime;
        var draftFromTranscript = _pendingFromTranscript;
        var draftLastCopy = _lastCopyTrack;

        EntryInput.Text = "";           // start clean so the bubble becomes its own entry
        _pendingEntryTime = null;
        _lastCopyTrack = -1;
        CopyBubbleToLogger(track, seg); // stamps at the bubble's time (no new-log offset)
        SubmitEntry();

        // Put the user's in-progress draft back exactly as it was.
        if (!string.IsNullOrEmpty(draft))
        {
            EntryInput.Text = draft;
            _pendingEntryTime = draftPending;
            _pendingFromTranscript = draftFromTranscript;
            _lastCopyTrack = draftLastCopy;
            EntryInput.CaretIndex = draft.Length;
            UpdatePendingHint();
        }
    }

    // ---- Dictation (mic → whisper) -----------------------------------------

    private void SetDictationStatus(string text)
    {
        DictationStatus.Text = text;
        DictationStatusRow.IsVisible = !string.IsNullOrEmpty(text);

        // Auto-clear transient messages ("nothing transcribed", "Transcribing…", errors) after 5s so
        // they don't linger. The live "● Recording…" line is NOT transient — it stays until recording
        // actually stops. Each new status resets the 5s window.
        _dictationDismissTimer?.Stop();
        var transient = !string.IsNullOrEmpty(text) && !text.StartsWith("●");
        if (transient)
        {
            _dictationDismissTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _dictationDismissTimer.Tick -= OnDictationDismissTick;
            _dictationDismissTimer.Tick += OnDictationDismissTick;
            _dictationDismissTimer.Start();
        }
    }

    private void OnDictationDismissTick(object? sender, EventArgs e)
    {
        _dictationDismissTimer?.Stop();
        SetDictationStatus("");
    }

    // Hotkey "D" / mic button: toggle recording. On stop, resume playback immediately and transcribe in
    // the background, then add the result as a log stamped at the moment recording started.
    private async void ToggleDictation()
    {
        if (_dictationBusy) return; // ignore re-entry while starting/stopping (transcription runs detached)
        var ffmpeg = ToolPath("ffmpeg.exe");
        if (ffmpeg == null) { SetDictationStatus("ffmpeg not found — can't dictate."); return; }
        _dictationBusy = true;
        try
        {
            if (_dictation.IsRecording)
            {
                var wav = _dictation.Stop();
                MicBtn.IsChecked = false;
                // Resume immediately (only if the video was playing when we paused it) so the user can keep
                // watching while transcription runs in the background.
                if (_dictationWasPlaying) try { _mpv?.Pause.Set(false); } catch { /* ignore */ }
                var startT = _dictationStartT ?? CurrentTime();
                _dictationStartT = null;
                if (wav == null) { SetDictationStatus("Dictation failed — no audio captured."); return; }
                SetDictationStatus("Transcribing dictation…");
                _ = TranscribeAndAddAsync(wav, startT); // detached → UI free immediately; safe to dictate again
            }
            else
            {
                _dictationWasPlaying = IsPlaying();
                _dictationStartT = CurrentTime();
                if (AutoPauseBtn.IsChecked == true) try { _mpv?.Pause.Set(true); } catch { /* ignore */ }
                var mic = _settings.DictationMic;
                var ok = await Task.Run(() => _dictation.Start(ffmpeg, mic)); // enumeration off the UI thread
                if (!ok)
                {
                    _dictationStartT = null;
                    MicBtn.IsChecked = false;
                    if (_dictationWasPlaying) try { _mpv?.Pause.Set(false); } catch { /* ignore */ }
                    SetDictationStatus("No microphone found (check Settings → Microphone).");
                    return;
                }
                MicBtn.IsChecked = true;
                SetDictationStatus("● Recording — press D (or the mic) again to stop.");
            }
        }
        finally { _dictationBusy = false; }
    }

    // Transcribe a recorded WAV in the background and add it as a log. Detached from ToggleDictation so the
    // user can immediately resume watching (and start another dictation while this one transcribes).
    private async Task TranscribeAndAddAsync(string wav, double startT)
    {
        try
        {
            string? text;
            try { text = await Task.Run(() => TranscribeWavToText(wav)); }
            catch { text = null; }
            try { File.Delete(wav); } catch { /* ignore */ }

            if (string.IsNullOrWhiteSpace(text))
            {
                if (!_dictation.IsRecording) SetDictationStatus("Dictation: nothing transcribed.");
                return;
            }
            if (_settings.AutoCorrectEnabled) text = AutoCorrect.CorrectAll(text);
            if (_settings.AutoCapitalizeEnabled) text = AutoCorrect.CapitalizeSentences(text);
            var entry = new LogEntry { T = startT, Text = text.Trim(), OffsetSeconds = OffsetAt(startT) };
            var surviving = InsertEntrySorted(entry);
            ScrollEntryIntoCenter(surviving);
            MarkDirty();
            _ = SaveMainFileSilentAsync();
            if (!_dictation.IsRecording) SetDictationStatus(""); // don't wipe a new recording's status
        }
        catch { /* never let a detached task crash the app */ }
    }

    // Run the whisper sidecar on an already-16kHz-mono WAV and join the segment text. Background-thread only.
    private string? TranscribeWavToText(string wav)
    {
        var cfgPath = WhisperConfigFile();
        EngineConfig? cfg = null;
        if (cfgPath != null)
            try { cfg = JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(cfgPath), EngineJsonOpts); } catch { cfg = null; }
        // Resolved after the config is read: which script to run is the config's to decide.
        var script = WhisperFile(EngineScriptName(cfg));
        if (cfg == null || script == null || string.IsNullOrEmpty(cfg.Python) || !File.Exists(cfg.Python)) return null;

        var tmpJson = Path.Combine(Path.GetTempPath(), $"fr_dictate_{Guid.NewGuid():N}.json");
        try
        {
            var pe = new ProcessStartInfo
            {
                FileName = cfg.Python, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory,
            };
            foreach (var a in new[] { script, "--audio", wav, "--out", tmpJson,
                                      "--model", cfg.Model, "--device", cfg.Device, "--compute", cfg.Compute })
                pe.ArgumentList.Add(a);
            ShareToolPath(pe);
            using (var p = ChildProcesses.Start(pe))
            {
                _ = p.StandardError.ReadToEndAsync();
                _ = p.StandardOutput.ReadToEndAsync();
                p.WaitForExit();
                if (p.ExitCode != 0) return null;
            }
            if (!File.Exists(tmpJson)) return null;
            var eo = JsonSerializer.Deserialize<EngineOut>(File.ReadAllText(tmpJson), EngineJsonOpts);
            if (eo == null) return null;
            return string.Join(" ", eo.Segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
        }
        catch { return null; }
        finally { try { File.Delete(tmpJson); } catch { } }
    }

    // Copy a transcript bubble into the manual-log box: quote it, optionally prefix the speaker, stamp
    // it at the bubble's time, and focus the box so the user can edit then Enter to log.
    private void CopyBubbleToLogger(int track, TranscriptSegment s)
    {
        var prefix = "";
        if (track == 1 && _settings.Track2PrefixEnabled && !string.IsNullOrWhiteSpace(_settings.Track2Prefix))
            prefix = _settings.Track2Prefix.Trim() + " ";
        else if (track == 2 && _settings.Track3PrefixEnabled && !string.IsNullOrWhiteSpace(_settings.Track3Prefix))
            prefix = _settings.Track3Prefix.Trim() + " ";
        var snippet = $"{prefix}\"{s.Text}\"";
        var existing = EntryInput.Text ?? "";
        var wasEmpty = string.IsNullOrWhiteSpace(existing);
        var trimmed = existing.TrimEnd();
        string combined;
        // Same-track consecutive copy → MERGE into the previous quote (drop the in-between "" + repeated prefix):
        //   Me: "first"  + copy another mic bubble  →  Me: "first second"
        // A different track (or a manual edit that broke the trailing quote) appends a fresh prefixed quote.
        if (!wasEmpty && _lastCopyTrack == track && trimmed.EndsWith("\""))
            combined = trimmed[..^1].TrimEnd() + " " + s.Text + "\"";
        else
            combined = wasEmpty ? snippet : trimmed + " " + snippet;
        _lastCopyTrack = track;
        EntryInput.Text = combined;      // fires OnEntryTextChanged (stamps "now" + maybe auto-pauses)
        if (wasEmpty) { _pendingEntryTime = s.Start; _pendingFromTranscript = true; } // bubble's exact time, no offset
        _autoEdits.Clear(); // a programmatic edit invalidates any pending auto-edit undo
        UpdatePendingHint();
        EntryInput.Focus();
        EntryInput.CaretIndex = combined.Length;
        _ = SetClipboardAsync(snippet);  // also place the snippet on the clipboard
    }

    // "T" — start a manual log: stamp the moment now, pause immediately (if auto-pause is on), focus box.
    private void StartManualLog()
    {
        _pendingEntryTime = CurrentTime();
        _pendingFromTranscript = false; // manual log → eligible for the new-log offset
        UpdatePendingHint();
        if (AutoPauseBtn.IsChecked == true)
            try { _mpv?.Pause.Set(true); } catch { /* unavailable before load */ }
        EntryInput.Focus();
    }

    // "E" — edit the most recently created log (pausing first if auto-pause is on).
    private void EditLastLog()
    {
        if (_entries.Count == 0) return;
        var last = _entries[0];
        foreach (var e in _entries) if (e.CreatedAtUtc >= last.CreatedAtUtc) last = e;
        // Remember whether the video was playing so committing the edit (Enter) can resume it.
        _editResumeEntry = null;
        if (AutoPauseBtn.IsChecked == true)
        {
            _editResumeWasPlaying = IsPlaying();
            _editResumeEntry = last;
            try { _mpv?.Pause.Set(true); } catch { /* unavailable before load */ }
        }
        foreach (var x in _entries) if (x.IsEditing && !ReferenceEquals(x, last)) x.IsEditing = false;
        last.IsEditing = true;
        foreach (var g in _groups) if (g.Entries.Contains(last)) { g.IsExpanded = true; break; }
        ScrollEntryIntoCenter(last);
        FocusEntryEditor(last);
    }

    private void SubmitEntry()
    {
        var text = (EntryInput.Text ?? "").Trim();
        // Autocorrect + capitalize the whole entry on submit so the last word (and any words finished
        // without a trailing space) get fixed/capitalized too.
        if (_settings.AutoCorrectEnabled) text = AutoCorrect.CorrectAll(text);
        if (_settings.AutoCapitalizeEnabled) text = AutoCorrect.CapitalizeSentences(text);
        var stamp = _pendingEntryTime ?? CurrentTime();
        // New-log offset: shift a MANUAL log's time by the configured ±seconds (e.g. −1 so it lands 1 s before
        // you logged it). Never applied to text copied from a transcript bubble (that keeps the bubble's time).
        if (!_pendingFromTranscript && _settings.NewLogOffsetSec != 0)
            stamp = Math.Max(0, stamp + _settings.NewLogOffsetSec);
        EntryInput.Text = "";
        _autoEdits.Clear();
        _pendingEntryTime = null;
        _pendingFromTranscript = false;
        _lastCopyTrack = -1; // new entry → next bubble copy starts a fresh quote
        UpdatePendingHint();
        if (text.Length == 0) return;

        var entry = new LogEntry { T = stamp, Text = text, OffsetSeconds = OffsetAt(stamp) };
        var surviving = InsertEntrySorted(entry);
        ScrollEntryIntoCenter(surviving); // bring the new (or merged-into) log into the middle of the list

        // Persist immediately after every manual log so a crash never loses one (silent main-file save
        // only — the versioned backups are made by the minute timer, not per-log). The row already
        // appears instantly via the incremental group insert, and the save's UI-thread cost is just the
        // cached DTO build (the compress + write runs off-thread), so this no longer causes the old lag.
        _ = SaveMainFileSilentAsync();

        // With Auto-pause on, logging an entry means you're done writing: resume playback and drop
        // focus out of the text box so typing stops there (Space etc. go back to transport).
        if (AutoPauseBtn.IsChecked == true)
        {
            try { _mpv?.Pause.Set(false); } catch { /* unavailable before load */ }
            Timeline.Focus();
        }
    }

    // Inserts the entry (or merges it into a nearby one). Returns the entry that actually ended up in the
    // list — the merge target when merged, else the inserted entry — so callers can scroll to the right row.
    private LogEntry InsertEntrySorted(LogEntry entry)
    {
        // Merge into an existing entry within the merge window (configurable). _entries is kept sorted
        // by T, so the first one within the window is the earliest — we keep it (or pull its time back
        // to the new one if the new entry is earlier) and append the text.
        var win = _settings.LogMergeWindowSec;
        if (win > 0)
        {
            foreach (var e in _entries)
            {
                if (Math.Abs(e.T - entry.T) > win) continue;
                // Record the original parts so the merge can be split back apart later.
                var parts = PartsOf(e); parts.AddRange(PartsOf(entry));
                parts.Sort((a, b) => a.T.CompareTo(b.T));
                e.MergedParts = parts;
                e.Text = string.Join("\n", parts.Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                var movedEarlier = entry.T < e.T;
                if (movedEarlier) { e.T = entry.T; e.OffsetSeconds = OffsetAt(entry.T); }
                // T may have changed → reposition within the sorted list.
                _entries.Remove(e);
                var j0 = 0; while (j0 < _entries.Count && _entries[j0].T <= e.T) j0++;
                _entries.Insert(j0, e);
                // The merged entry's text/time update in place via data binding. Only when its time moved
                // it earlier (possibly reordering rows / crossing a clip) do we pay for a full rebuild;
                // the common merge (a later log folded into an earlier one) just refreshes markers.
                if (movedEarlier) RebuildGroupsAndMarkers();
                else { UpdateTimelineMarkers(); RefreshSearchIfActive(); }
                MarkDirty();
                return e;
            }
        }

        var i = 0;
        while (i < _entries.Count && _entries[i].T <= entry.T) i++;
        _entries.Insert(i, entry);
        InsertEntryIntoGroups(entry);
        MarkDirty();
        return entry;
    }

    // Incrementally add ONE new entry to the right clip group's ObservableCollection instead of clearing
    // and rebuilding every group (which, with the non-virtualized list, re-creates the whole visual tree
    // — the ~1s lag when committing a log). Falls back to a full rebuild for cases the fast path can't
    // place safely (group not built yet, or an entry outside every clip → the "(outside clips)" group).
    private void InsertEntryIntoGroups(LogEntry entry)
    {
        var target = FindGroupForTime(entry.T);
        if (target == null) { RebuildGroupsAndMarkers(); return; }
        var ins = 0;
        while (ins < target.Entries.Count && target.Entries[ins].T <= entry.T) ins++;
        target.Entries.Insert(ins, entry);
        target.IsExpanded = true; // make sure the freshly-logged entry is visible
        UpdateTimelineMarkers();
        RefreshSearchIfActive();
        // Layout changed → refresh the sticky-folder label and the playhead line once realized.
        Dispatcher.UIThread.Post(() => { UpdateStickyFolder(); UpdateLogPlayhead(force: true); }, DispatcherPriority.Loaded);
    }

    // The clip group that an entry with the given time belongs to, mirroring RebuildGroupsAndMarkers'
    // grouping. Returns null when the fast path shouldn't be used (no group built yet / outside all clips).
    private ClipGroup? FindGroupForTime(double t)
    {
        if (_clipStarts.Length == 0)
            return _groups.Count >= 1 ? _groups[0] : null; // the sole "(no clip)" group, or none yet
        for (var c = 0; c < _clipStarts.Length; c++)
        {
            var end = _clipStarts[c] + (c < _clipDurations.Length ? _clipDurations[c] : double.PositiveInfinity);
            if (t >= _clipStarts[c] && t < end)
                return c < _groups.Count ? _groups[c] : null; // _groups[c] is clip c (orphans, if any, sit after)
        }
        return null; // outside every clip → let the full rebuild manage the "(outside clips)" group
    }

    // Re-apply the active log search after the entry set changes (keeps match list/count/highlights live).
    private void RefreshSearchIfActive()
    {
        if (!_rebuildingSearch && LogSearchBox is { } sb && !string.IsNullOrEmpty(sb.Text))
        {
            _rebuildingSearch = true;
            try { ApplyLogSearch(scrollToFirst: false); }
            catch (Exception ex) { DiagnosticsLogger.LogException("search:refresh", ex); }
            finally { _rebuildingSearch = false; }
        }
    }

    // Name of the clip/folder the given time falls in (for the always-visible "current folder" label).
    private string CurrentClipName(double t)
    {
        if (_clipStarts.Length == 0) return "—";
        for (var c = _clipStarts.Length - 1; c >= 0; c--)
            if (t >= _clipStarts[c]) return c < _clipNames.Length ? _clipNames[c] : $"Clip {c + 1}";
        return _clipNames.Length > 0 ? _clipNames[0] : "—";
    }

    private void RebuildGroupsAndMarkers()
    {
        _groups.Clear();
        if (_clipStarts.Length == 0)
        {
            var sole = new ClipGroup { Name = "(no clip)", Start = 0 };
            foreach (var e in _entries.OrderBy(x => x.T)) sole.Entries.Add(e);
            if (sole.Entries.Count > 0) _groups.Add(sole);
        }
        else
        {
            for (var c = 0; c < _clipStarts.Length; c++)
            {
                var baseName = c < _clipNames.Length ? _clipNames[c] : $"Clip {c + 1}";
                // Append the clip's position in the sequence, e.g. "... (25/57)".
                var name = $"{baseName} ({c + 1}/{_clipStarts.Length})";
                var start = _clipStarts[c];
                var end = start + (c < _clipDurations.Length ? _clipDurations[c] : double.PositiveInfinity);
                var grp = new ClipGroup { Name = name, Start = start };
                foreach (var e in _entries.Where(x => x.T >= start && x.T < end).OrderBy(x => x.T))
                    grp.Entries.Add(e);
                _groups.Add(grp);
            }
            // catch entries that fall outside any clip (e.g. while no footage is loaded)
            var orphans = _entries.Where(e =>
                !_clipStarts.Zip(_clipDurations, (s, d) => (s, e: s + d)).Any(p => e.T >= p.s && e.T < p.e)
            ).OrderBy(e => e.T).ToList();
            if (orphans.Count > 0)
            {
                var grp = new ClipGroup { Name = "(outside clips)", Start = -1 };
                foreach (var e in orphans) grp.Entries.Add(e);
                _groups.Add(grp);
            }
        }
        UpdateTimelineMarkers();

        // On project open, once the groups are populated, scroll to the furthest-in log (largest time).
        if (_scrollToFurthestPending && _entries.Count > 0)
        {
            _scrollToFurthestPending = false;
            var furthest = _entries.Aggregate((a, b) => b.T >= a.T ? b : a);
            foreach (var g in _groups) if (g.Entries.Contains(furthest)) { g.IsExpanded = true; break; }
            ScrollEntryIntoCenter(furthest);
        }

        // Entries changed → refresh the active search (its match list/count/highlights would otherwise go
        // stale after a delete/merge/split/edit).
        RefreshSearchIfActive();

        // Layout changed → refresh the sticky-folder label and the playhead line once realized.
        Dispatcher.UIThread.Post(() => { UpdateStickyFolder(); UpdateLogPlayhead(force: true); }, DispatcherPriority.Loaded);
    }
    private bool _rebuildingSearch;

    private void UpdateTimelineMarkers()
    {
        var times = _entries.Select(e => e.T).ToArray();
        var colors = _entries.Select(e => e.Color).ToArray();
        var texts = _entries.Select(e => e.Text).ToArray();
        Timeline.SetMarkers(times, texts, colors);
        TranscriptTimeline.SetMarkers(times, texts, colors); // marker tags + hover on the transcript too

        var (dTimes, dTexts, dColors) = ResolveDayMarkers();
        Timeline.SetDayMarkers(dTimes, dTexts, dColors);
        TranscriptTimeline.SetDayMarkers(dTimes, dTexts, dColors);
    }

    /// <summary>
    /// Day markers store the clip they belong to, not a time, so their positions are derived from the
    /// current clip layout every time it changes. A marker whose clip has been deleted resolves to
    /// nothing and simply stops being drawn.
    /// </summary>
    private (double[] Times, string[] Texts, string[] Colors) ResolveDayMarkers()
    {
        if (_dayMarkers.Count == 0 || _currentSources == null || _clipStarts.Length == 0)
            return (Array.Empty<double>(), Array.Empty<string>(), Array.Empty<string>());

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _currentSources.Count; i++) index[_currentSources[i]] = i;

        var times = new List<double>(_dayMarkers.Count);
        var texts = new List<string>(_dayMarkers.Count);
        var colors = new List<string>(_dayMarkers.Count);
        _drawnDayMarkers.Clear();
        foreach (var m in _dayMarkers)
        {
            if (!index.TryGetValue(m.SourcePath, out var ci) || ci >= _clipStarts.Length) continue;
            times.Add(_clipStarts[ci]);
            texts.Add(m.Text);
            colors.Add(string.IsNullOrEmpty(m.Color) ? DayMarker.DefaultColor : m.Color);
            _drawnDayMarkers.Add(m); // keeps hit-test indices aligned with what is actually drawn
        }
        return (times.ToArray(), texts.ToArray(), colors.ToArray());
    }

    // Live "Logs · N" count in the manual-log header.
    private void UpdateLogCount()
    {
        if (LogsHeader == null) return;
        LogsHeader.Text = _entries.Count > 0 ? $"Logs · {_entries.Count}" : "Logs";
    }

    // ⋮ menu → Import logs from text. Paste a block; each timecoded line becomes a log entry (added to the
    // current ones). A 2-part timecode (e.g. 8:00) is only prompted for when the block actually contains one.
    private async Task ImportLogsFromTextAsync()
    {
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit…"; return; } // serialize edits
        if (!await EnsureProjectAsync()) return; // a saved project must exist to hold the logs
        var result = await new ImportLogsWindow().ShowDialog<ImportLogsResult?>(this);
        if (result == null) return;
        // A clip insert/reorder may have started while the dialog was open — its deferred remap rewrites every
        // entry's time through the OLD clip layout, so don't add new entries into that race; bail and let the
        // user re-import once the timeline has settled.
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit — try the import again"; return; }
        var lines = LogImport.Parse(result.Text, result.TwoPartIsHoursMinutes);
        if (lines.Count == 0) { ClipInfo.Text = "no timecoded log lines found in the pasted text"; return; }

        foreach (var ln in lines)
        {
            var t = VideoTimeFromDisplay(ln.Seconds);
            var entry = new LogEntry { T = t, Text = ln.Text, OffsetSeconds = OffsetAt(t) };
            var i = 0;
            while (i < _entries.Count && _entries[i].T <= t) i++;
            _entries.Insert(i, entry); // bulk add (no auto-merge — each pasted line is its own entry)
        }
        RebuildGroupsAndMarkers();
        MarkDirty();
        _ = SaveMainFileSilentAsync();
        ClipInfo.Text = $"imported {lines.Count} log{(lines.Count == 1 ? "" : "s")}";
    }

    // Convert a pasted DISPLAY timecode (= what the log list shows = video time + timer offset) back to the
    // canonical video time T, inverting the sync/offset model so re-imported "Copy all" output and timer-based
    // logs land where they read. With no sync points this is just (display − global timer offset).
    private double VideoTimeFromDisplay(double display)
    {
        if (_offsetAnchors.Count == 0) return Math.Max(0, display - _timerOffset);
        // Invert the piecewise offset robustly. TimerValue is NOT guaranteed monotonic with VideoTime (a timer
        // reset or typo can make it go backwards), so an early break on TimerValue is unsafe. Instead, for each
        // anchor compute the candidate video time and keep the one that best reproduces `display` under the
        // forward OffsetAt map (exact round-trip in the normal case; nearest otherwise).
        double best = Math.Max(0, display - _timerOffset);
        var bestErr = double.MaxValue;
        foreach (var a in _offsetAnchors)
        {
            var candidate = Math.Max(0, a.VideoTime + (display - a.TimerValue));
            var err = Math.Abs(candidate + OffsetAt(candidate) - display);
            if (err <= bestErr) { bestErr = err; best = candidate; } // <= biases toward later anchors on ties
        }
        return best;
    }

    // Click a video-timeline marker → center its manual log entry (expanding its clip if collapsed).
    private void OnMarkerClicked(int markerIndex)
    {
        if (markerIndex < 0 || markerIndex >= _entries.Count) return;
        var entry = _entries[markerIndex];
        SeekTo(entry.T);
        PosText.Text = Fmt(entry.T);
        foreach (var g in _groups) if (g.Entries.Contains(entry)) { g.IsExpanded = true; break; }
        ScrollEntryIntoCenter(entry);
    }

    // Drag a video-timeline marker → retime its manual log entry (and re-sort/regroup by the new time).
    private void OnMarkerMoved(int markerIndex, double newTime)
    {
        if (markerIndex < 0 || markerIndex >= _entries.Count) return;
        var entry = _entries[markerIndex];
        entry.T = Math.Max(0, newTime);
        entry.OffsetSeconds = OffsetAt(entry.T);
        // Keep _entries sorted by T — markers and clip groups are derived in this order.
        _entries.RemoveAt(markerIndex);
        var i = 0;
        while (i < _entries.Count && _entries[i].T <= entry.T) i++;
        _entries.Insert(i, entry);

        // Incremental regroup instead of RebuildGroupsAndMarkers(): pull the entry from its current clip group
        // and drop it into the one for its new time. The full rebuild re-created the entire (non-virtualised)
        // GroupsList visual tree on every drag-release — that was the lag spike.
        var target = FindGroupForTime(entry.T);
        if (target == null) RebuildGroupsAndMarkers(); // moved outside every clip → full rebuild (rare)
        else
        {
            foreach (var g in _groups) if (g.Entries.Remove(entry)) break;
            var ins = 0;
            while (ins < target.Entries.Count && target.Entries[ins].T <= entry.T) ins++;
            target.Entries.Insert(ins, entry);
            target.IsExpanded = true;
            UpdateTimelineMarkers();
            RefreshSearchIfActive();
        }
        MarkDirty();
        // No SeekTo: retiming a marker is a log edit, not playback navigation — the playhead stays put.
    }

    private static readonly (string Name, string Hex)[] MarkerColors =
    {
        ("Amber", "#FFCB5C"), ("Red", "#FF5A5A"), ("Green", "#6BE08A"),
        ("Blue", "#5AA8FF"), ("Purple", "#C58CFF"), ("White", "#FFFFFF"),
    };

    private void OnDayMarkerRightClicked(int idx)
    {
        if (idx < 0 || idx >= _drawnDayMarkers.Count) return;
        var marker = _drawnDayMarkers[idx];

        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += async (_, _) =>
        {
            var dlg = new TextPromptWindow("Day marker label", marker.Text);
            var name = await dlg.ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(name)) return;
            marker.Text = name;
            UpdateTimelineMarkers();
            MarkDirty();
        };
        menu.Items.Add(rename);

        foreach (var (name, hex) in MarkerColors)
        {
            var swatch = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                Background = Brush.Parse(hex), Margin = new Avalonia.Thickness(0, 0, 8, 0),
            };
            var label = string.Equals(hex, DayMarker.DefaultColor, StringComparison.OrdinalIgnoreCase) ? $"{name} (default)" : name;
            var item = new MenuItem
            {
                Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } },
            };
            var chosen = hex;
            item.Click += (_, _) => { marker.Color = chosen; UpdateTimelineMarkers(); MarkDirty(); };
            menu.Items.Add(item);
        }

        var del = new MenuItem { Header = "Delete day marker" };
        del.Click += (_, _) =>
        {
            _dayMarkers.Remove(marker);
            UpdateTimelineMarkers();
            MarkDirty();
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(del);

        menu.Open(Timeline);
    }

    private void OnMarkerRightClicked(int markerIdx)
    {
        if (markerIdx < 0 || markerIdx >= _entries.Count) return;
        ShowColorMenu(_entries[markerIdx], Timeline);
    }

    private void UpdatePendingHint()
        => PendingHint.Text = _pendingEntryTime is double pt
            ? $"will log at {LogEntry.FormatTc(pt + OffsetAt(pt))}"
            : "";

    // Piecewise offset at a given video time: use the last sync point at or before t (or the first one
    // if t precedes them all). With no sync points, fall back to the legacy single offset.
    private double OffsetAt(double videoTime)
    {
        if (_offsetAnchors.Count == 0) return _timerOffset;
        var chosen = _offsetAnchors[0];
        foreach (var a in _offsetAnchors)
        {
            if (a.VideoTime <= videoTime) chosen = a;
            else break;
        }
        return chosen.TimerValue - chosen.VideoTime;
    }

    // Add (or replace, if within ~0.5 s) a sync point anchoring the current frame to the typed reading.
    private void SetOffsetFromInput()
    {
        if (TryParseTimecode(OffsetInput.Text, out var timerValue))
        {
            var vt = CurrentTime();
            _offsetAnchors.RemoveAll(a => Math.Abs(a.VideoTime - vt) < 0.5);
            _offsetAnchors.Add((vt, timerValue));
            _offsetAnchors.Sort((x, y) => x.VideoTime.CompareTo(y.VideoTime));
            OffsetInput.Text = "";
            RefreshEntryOffsets();
            UpdateOffsetStatus();
            MarkDirty();
        }
        else
        {
            OffsetStatus.Text = "Couldn't parse — use H:MM:SS, M:SS, or seconds.";
        }
    }

    private void ClearOffset()
    {
        _offsetAnchors.Clear();
        _timerOffset = 0;
        OffsetInput.Text = "";
        RefreshEntryOffsets();
        UpdateOffsetStatus();
        MarkDirty();
    }

    private void RefreshEntryOffsets()
    {
        foreach (var e in _entries) e.OffsetSeconds = OffsetAt(e.T);
        UpdatePendingHint();
    }

    private void UpdateOffsetStatus()
    {
        if (_offsetAnchors.Count == 0)
        {
            if (Math.Abs(_timerOffset) < 1e-6)
                OffsetStatus.Text = "No sync points — timestamps match the video clock.";
            else
            {
                var s = _timerOffset >= 0 ? "+" : "−";
                OffsetStatus.Text = $"Offset {s}{LogEntry.FormatTc(Math.Abs(_timerOffset))} — displayed times shifted to your timer.";
            }
        }
        else
        {
            var off = OffsetAt(CurrentTime());
            var sign = off >= 0 ? "+" : "−";
            var n = _offsetAnchors.Count;
            OffsetStatus.Text = $"{n} sync point{(n == 1 ? "" : "s")} — displayed time follows your on-screen timer across pauses (here: {sign}{LogEntry.FormatTc(Math.Abs(off))}).";
        }
        RebuildSyncPointsPanel();
    }

    // Expandable detail list of sync points, each with editable video-time / timer-value boxes and a
    // delete button. Rebuilt whenever the anchors change.
    private bool _syncPanelBusy;
    private void RebuildSyncPointsPanel()
    {
        if (SyncPointsPanel == null) return;
        _syncPanelBusy = true;
        SyncPointsPanel.Children.Clear();
        // The sync-points list lives inside the ⋮ flyout now; show a small count header when non-empty.
        if (_offsetAnchors.Count > 0)
            SyncPointsPanel.Children.Add(new TextBlock
            {
                Text = $"Sync points ({_offsetAnchors.Count})",
                Foreground = Brush.Parse("#9A9AA8"), FontSize = 11, Margin = new Avalonia.Thickness(0, 2, 0, 2),
            });

        for (var i = 0; i < _offsetAnchors.Count; i++)
        {
            var idx = i;
            var (vt, tv) = _offsetAnchors[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
            var at = new TextBlock { Text = "@", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#7E7E8C"), FontSize = 11, Margin = new Avalonia.Thickness(0, 0, 4, 0) };
            var vBox = new TextBox { Text = LogEntry.FormatTc(vt), Width = 80, FontFamily = new FontFamily("Consolas"), FontSize = 11, Padding = new Avalonia.Thickness(4, 2), [ToolTip.TipProperty] = "Video time of this sync point" };
            var arrow = new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#7E7E8C"), Margin = new Avalonia.Thickness(4, 0, 4, 0) };
            var tBox = new TextBox { Text = LogEntry.FormatTc(tv), Width = 80, FontFamily = new FontFamily("Consolas"), FontSize = 11, Padding = new Avalonia.Thickness(4, 2), [ToolTip.TipProperty] = "On-screen timer reading at that frame" };
            var del = new Button { Content = "✕", FontSize = 11, Padding = new Avalonia.Thickness(6, 2), Focusable = false, [ToolTip.TipProperty] = "Delete this sync point" };
            Grid.SetColumn(at, 0); Grid.SetColumn(vBox, 1); Grid.SetColumn(arrow, 2); Grid.SetColumn(tBox, 3); Grid.SetColumn(del, 5);

            void Commit()
            {
                if (_syncPanelBusy || idx >= _offsetAnchors.Count) return;
                if (TryParseTimecode(vBox.Text, out var nv) && TryParseTimecode(tBox.Text, out var ntv))
                {
                    _offsetAnchors[idx] = (Math.Max(0, nv), Math.Max(0, ntv));
                    _offsetAnchors.Sort((x, y) => x.VideoTime.CompareTo(y.VideoTime));
                    RefreshEntryOffsets(); MarkDirty(); UpdateOffsetStatus(); // rebuilds the panel
                }
                else { vBox.Text = LogEntry.FormatTc(_offsetAnchors[idx].VideoTime); tBox.Text = LogEntry.FormatTc(_offsetAnchors[idx].TimerValue); }
            }
            vBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            tBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            vBox.LostFocus += (_, _) => Commit();
            tBox.LostFocus += (_, _) => Commit();
            del.Click += (_, _) =>
            {
                if (_syncPanelBusy || idx >= _offsetAnchors.Count) return;
                _offsetAnchors.RemoveAt(idx);
                RefreshEntryOffsets(); MarkDirty(); UpdateOffsetStatus(); // rebuilds the panel
            };

            row.Children.Add(at); row.Children.Add(vBox); row.Children.Add(arrow); row.Children.Add(tBox); row.Children.Add(del);
            SyncPointsPanel.Children.Add(row);
        }
        _syncPanelBusy = false;
    }

    private static bool TryParseTimecode(string? s, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split(':');
        var ci = CultureInfo.InvariantCulture;
        if (parts.Length == 1)
            return double.TryParse(parts[0], NumberStyles.Float, ci, out seconds);
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) &&
            double.TryParse(parts[1], NumberStyles.Float, ci, out var s2))
        { seconds = m * 60 + s2; return true; }
        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m3) &&
            double.TryParse(parts[2], NumberStyles.Float, ci, out var s3))
        { seconds = h * 3600 + m3 * 60 + s3; return true; }
        return false;
    }

    // One copied line for an entry: "H:MM:SS<tab>text", or just the text when timestamps are off.
    private string EntryLine(LogEntry e)
        => _settings.CopyTimestamps ? $"{LogEntry.FormatTc(e.T + OffsetAt(e.T))}\t{e.Text}" : e.Text;

    // Build the clipboard text for a set of folders, honoring the copy-options (headers, timestamps,
    // only-uncopied). With headers off, entries are flat (no indentation). Reports which entries were
    // included via <paramref name="included"/>.
    private string BuildCopyText(IEnumerable<ClipGroup> groups, bool onlyUncopied, out List<LogEntry> included)
    {
        included = new List<LogEntry>();
        var headers = _settings.CopyFolderHeaders;
        var indent = headers ? "\t" : "";
        var sb = new StringBuilder();

        // Day headings sit at column 0 above the clip folders; the existing layout below them is
        // untouched, so turning this off reproduces the old output exactly.
        var (dayTimes, dayTexts, _) = _settings.CopyDayHeadings
            ? ResolveDayMarkers()
            : (Array.Empty<double>(), Array.Empty<string>(), Array.Empty<string>());
        var lastDay = -1;

        foreach (var grp in groups)
        {
            var entries = grp.Entries.Where(e => !onlyUncopied || !e.Copied).ToList();
            if (entries.Count == 0) continue;

            // The day a clip belongs to is the last divider at or before its start.
            var day = -1;
            for (var i = 0; i < dayTimes.Length; i++)
                if (dayTimes[i] <= grp.Start + 0.001) day = i; else break;
            if (day >= 0 && day != lastDay)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(dayTexts[day]);
                lastDay = day;
            }

            if (headers) sb.AppendLine(grp.Name);
            foreach (var e in entries) { sb.AppendLine(indent + EntryLine(e)); included.Add(e); }
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private async void OnCopyEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not LogEntry le) return;
        await SetClipboardAsync(EntryLine(le));
        b.Content = "✓";
        await System.Threading.Tasks.Task.Delay(700);
        b.Content = "⧉";
    }

    private async System.Threading.Tasks.Task CopyAllAsync()
    {
        if (_entries.Count == 0) { CopyAllBtn.Content = "nothing yet"; await ResetCopyAllLabel(); return; }
        if (_groups.Count == 0) RebuildGroupsAndMarkers();
        var onlyNew = _settings.CopyOnlyUncopied;
        var text = BuildCopyText(_groups, onlyNew, out var copied);
        if (copied.Count == 0)
        {
            CopyAllBtn.Content = onlyNew ? "all already copied" : "nothing yet";
            await ResetCopyAllLabel();
            return;
        }
        await SetClipboardAsync(text);
        // Strike the entries we just copied so the next Copy all only grabs new ones.
        foreach (var e in copied) e.Copied = true;
        MarkDirty();
        CopyAllBtn.Content = $"copied {copied.Count}";
        await ResetCopyAllLabel();
    }

    // ---- Copied (strikethrough) toggles ------------------------------------

    private void OnToggleEntryCopiedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LogEntry le }) { le.Copied = !le.Copied; MarkDirty(); }
    }

    private void OnToggleGroupCopiedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipGroup g }) return;
        var allCopied = g.Entries.Count > 0 && g.Entries.All(x => x.Copied);
        foreach (var x in g.Entries) x.Copied = !allCopied; // toggle the whole clip
        MarkDirty();
    }

    private void MarkAllCopied(bool copied)
    {
        foreach (var e in _entries) e.Copied = copied;
        MarkDirty();
    }

    // ---- Folder toggle / copy ----------------------------------------------

    private void OnToggleGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ClipGroup g })
            g.IsExpanded = !g.IsExpanded;
        // Expand/collapse shifts row positions — refresh the sticky label + playhead once re-laid-out.
        Dispatcher.UIThread.Post(() => { UpdateStickyFolder(); UpdateLogPlayhead(force: true); }, DispatcherPriority.Loaded);
    }

    private async void OnCopyGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipGroup g } b) return;
        // Explicit per-folder copy grabs every entry in the clip (regardless of struck state) and does
        // not mark them — it's a quick ad-hoc copy. Only "Copy all" drives the strikethrough workflow.
        var text = BuildCopyText(new[] { g }, onlyUncopied: false, out _);
        await SetClipboardAsync(text);
        var prev = b.Content;
        b.Content = $"copied {g.Entries.Count}";
        await Task.Delay(900);
        b.Content = prev;
    }

    // ---- Edit / delete entries ---------------------------------------------

    // Click a manual-log row to jump the playhead there. T is canonical video time (offset is
    // display-only), so we seek to T directly. The copy/edit/delete buttons mark their own clicks
    // handled, so this only fires when you click the timecode/text area.
    private void OnEntryRowClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: LogEntry le } c) return;
        // Right-click a log row → colour menu (same palette as timeline markers).
        if (e.GetCurrentPoint(c).Properties.IsRightButtonPressed)
        {
            ShowColorMenu(le, c);
            e.Handled = true;
            return;
        }
        SeekTo(le.T);
        PosText.Text = Fmt(le.T);
        // Optionally snap both timeline views to the clicked log, even under center-lock (CenterOn bypasses
        // the lock; under lock the next tick re-centres on the same playhead so it stays put).
        if (_settings.ClickLogSnapsView)
        {
            Timeline.CenterOn(le.T);
            TranscriptTimeline.CenterOn(le.T);
        }
    }

    // Toggle the Logs-tab "chrome" (header buttons + search bar + clip label) for more text room. The ⋮ menu
    // and the Logs title stay visible so the toggle is always reachable.
    private void ApplyLogChrome()
    {
        var show = !_settings.HideLogChrome;
        AutoPauseBtn.IsVisible = show;
        LogCenterLockBtn.IsVisible = show;
        CopyAllBtn.IsVisible = show;
        LogSearchRow.IsVisible = show;
        CurrentFolderBorder.IsVisible = show;
    }

    // Build + open the marker/log colour picker for an entry, anchored to `placement`.
    private void ShowColorMenu(LogEntry entry, Control placement)
    {
        var menu = new ContextMenu();
        foreach (var (name, hex) in MarkerColors)
        {
            var swatch = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                Background = Brush.Parse(hex), Margin = new Avalonia.Thickness(0, 0, 8, 0),
            };
            var label = string.Equals(hex, LogEntry.DefaultColor, StringComparison.OrdinalIgnoreCase) ? $"{name} (default)" : name;
            var item = new MenuItem
            {
                Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } },
            };
            var chosen = hex;
            item.Click += (_, _) => { entry.Color = chosen; UpdateTimelineMarkers(); MarkDirty(); };
            menu.Items.Add(item);
        }
        menu.Open(placement);
    }

    private void OnEditEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LogEntry le })
        {
            // close any other in-flight edits, then open this one
            foreach (var x in _entries) if (x.IsEditing && !ReferenceEquals(x, le)) x.IsEditing = false;
            le.IsEditing = true;
            FocusEntryEditor(le); // drop the cursor straight into the text box
        }
    }

    private readonly List<LogEntry> _logMatches = new();
    private int _logMatchIdx = -1;
    private DispatcherTimer? _logSearchTimer;
    private DispatcherTimer? _transcriptSearchTimer;

    // Run a search action defensively: a fault here used to propagate out of the event handler and take the
    // whole app down. Log it (diagnostics.log) and surface a status line instead.
    private void SafeSearch(string what, Action run)
    {
        try { run(); }
        catch (Exception ex)
        {
            DiagnosticsLogger.LogException($"search:{what}", ex);
            try { ClipInfo.Text = "search hit an error (logged to diagnostics.log)"; } catch { /* UI gone */ }
        }
    }

    // Coalesce keystrokes: only search once typing pauses (searching scans every entry and repaints the whole
    // non-virtualised log list, which is far too heavy to do per character on a large project).
    private void DebounceLogSearch()
    {
        _logSearchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _logSearchTimer.Stop();
        _logSearchTimer.Tick -= OnLogSearchTick;
        _logSearchTimer.Tick += OnLogSearchTick;
        _logSearchTimer.Start();
    }
    private void OnLogSearchTick(object? sender, EventArgs e)
    {
        _logSearchTimer?.Stop();
        SafeSearch("log", () => ApplyLogSearch(scrollToFirst: false));
    }

    private void DebounceTranscriptSearch()
    {
        _transcriptSearchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _transcriptSearchTimer.Stop();
        _transcriptSearchTimer.Tick -= OnTranscriptSearchTick;
        _transcriptSearchTimer.Tick += OnTranscriptSearchTick;
        _transcriptSearchTimer.Start();
    }
    private void OnTranscriptSearchTick(object? sender, EventArgs e)
    {
        _transcriptSearchTimer?.Stop();
        SafeSearch("transcript", () =>
        {
            var q = TranscriptSearchBox?.Text;
            TranscriptTimeline.SetSearch(q);
            var n = TranscriptTimeline.SearchMatchCount();
            if (SearchCountLabel != null)
                SearchCountLabel.Text = string.IsNullOrEmpty(q) ? "" : $"{n} match{(n == 1 ? "" : "es")}";
        });
    }

    // Highlight manual-log entries matching the search box; optionally jump to the first match.
    private void ApplyLogSearch(bool scrollToFirst)
    {
        var q = (LogSearchBox.Text ?? "").Trim();
        _logMatches.Clear();
        // Snapshot: setting SearchMatch raises PropertyChanged, and a handler reacting to it could otherwise
        // mutate _entries while we're iterating it (which throws and, from an event handler, kills the app).
        foreach (var e in _entries.ToList())
        {
            var m = q.Length > 0 && e.Text is { } t && t.Contains(q, StringComparison.OrdinalIgnoreCase);
            e.SearchMatch = m;
            if (m) _logMatches.Add(e);
        }
        _logMatchIdx = -1;
        UpdateLogSearchCount();
        if (scrollToFirst && _logMatches.Count > 0) NavigateLogMatch(1);
    }

    private void UpdateLogSearchCount()
    {
        var q = (LogSearchBox.Text ?? "").Trim();
        if (q.Length == 0) { LogSearchCount.Text = ""; return; }
        LogSearchCount.Text = _logMatches.Count == 0 ? "0"
            : $"{Math.Max(1, _logMatchIdx + 1)}/{_logMatches.Count}";
    }

    // Step to the next/previous matching entry (wraps around) and centre it.
    private void NavigateLogMatch(int dir)
    {
        if (_logMatches.Count == 0) return;
        var n = _logMatches.Count;
        var next = _logMatchIdx < 0 ? (dir > 0 ? 0 : n - 1) : (((_logMatchIdx + dir) % n) + n) % n;
        _logMatchIdx = next;
        var e = _logMatches[next];
        foreach (var g in _groups) if (g.Entries.Contains(e)) { g.IsExpanded = true; break; }
        ScrollEntryIntoCenter(e);
        UpdateLogSearchCount();
    }

    // ---- Manual-log scroll / focus helpers ---------------------------------

    // BFS the (non-virtualizing) GroupsList visual tree for the row container hosting this entry.
    private Control? FindEntryContainer(LogEntry entry)
    {
        var queue = new Queue<Visual>();
        queue.Enqueue(GroupsList);
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (v is ContentPresenter cp && ReferenceEquals(cp.DataContext, entry)) return cp;
            foreach (var child in v.GetVisualChildren()) queue.Enqueue(child);
        }
        return null;
    }

    // BFS for the container (group header or entry row) whose DataContext is `item`.
    private Control? FindContainer(object item)
    {
        var queue = new Queue<Visual>();
        queue.Enqueue(GroupsList);
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (v is ContentPresenter cp && ReferenceEquals(cp.DataContext, item)) return cp;
            foreach (var child in v.GetVisualChildren()) queue.Enqueue(child);
        }
        return null;
    }

    // ---- Manual-log discrete playhead + sticky folder ----------------------

    // Sticky-header current folder: show the clip whose header has scrolled off the top of the view
    // (i.e. the last group whose top is at/above the viewport top). If a folder header is still visible
    // on screen, the label shows the folder above it that you can no longer see.
    private void UpdateStickyFolder()
    {
        if (CurrentFolderText == null) return;
        if (_groups.Count == 0 || EntriesScroller.Content is not Visual content)
        {
            if (CurrentFolderText.Text != "—") CurrentFolderText.Text = "—";
            return;
        }
        var scrollTop = EntriesScroller.Offset.Y;
        // One visual-tree pass to map each group → its container (avoids an O(groups) BFS per scroll).
        var map = new Dictionary<object, Control>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<Visual>();
        queue.Enqueue(GroupsList);
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (v is ContentPresenter cp && cp.DataContext is ClipGroup) map[cp.DataContext!] = cp;
            foreach (var child in v.GetVisualChildren()) queue.Enqueue(child);
        }
        string? current = null;
        foreach (var g in _groups)
        {
            if (!map.TryGetValue(g, out var c)) continue;
            if (c.TranslatePoint(new Point(0, 0), content) is not { } p) continue;
            if (p.Y <= scrollTop + 1) current = g.Name; else break;
        }
        current ??= _groups[0].Name;   // nothing scrolled off yet → the top folder
        if (CurrentFolderText.Text != current) CurrentFolderText.Text = current;
    }

    // Snap the manual-log playhead line to the current playback time. It rests at the bottom of the
    // last log/folder you've reached (drawn at the top of the next one); past the last log it sits at
    // the bottom of the list. When lock-to-centre is on, the list auto-scrolls so the current point
    // stays centred (same centre ScrollEntryIntoCenter uses, so adding a log doesn't jump the view).
    // The `force` parameter is retained for call-site clarity; geometry is recomputed every call (the
    // timer ticks only ~8×/s, so a single visual-tree pass is cheap) — this avoids the stale-cache class
    // of bug where a geometry-only layout change (e.g. a row entering edit mode) moves anchors without
    // changing which item is active/next.
    // Logs whose time is within ±NearPlayheadSec of the playhead get a distinct row colour. _entries is
    // time-sorted, so the in-range block is contiguous: walk out from a binary search and only touch entries
    // whose flag actually CHANGES (the log list isn't virtualised — flipping every row each tick would stall).
    private const double NearPlayheadSec = 5.0;
    private int _nearFrom = -1, _nearTo = -1; // inclusive range currently flagged
    private int _nearCount = -1;              // _entries.Count when that range was computed
    private void UpdateNearPlayheadLogs(double pos)
    {
        // The entry set changed (add / delete / remap) → the cached indices no longer mean anything. Clear
        // every flag once and recompute from scratch.
        if (_entries.Count != _nearCount)
        {
            foreach (var e in _entries) e.NearPlayhead = false;
            _nearFrom = _nearTo = -1;
            _nearCount = _entries.Count;
        }

        int lo = 0, hi = _entries.Count;
        while (lo < hi) { var mid = (lo + hi) >> 1; if (_entries[mid].T < pos - NearPlayheadSec) lo = mid + 1; else hi = mid; }
        var from = lo;
        var to = from - 1;
        while (to + 1 < _entries.Count && _entries[to + 1].T <= pos + NearPlayheadSec) to++;

        if (from == _nearFrom && to == _nearTo) return; // unchanged → nothing to repaint
        // Clear the entries leaving the range, set the ones entering it.
        if (_nearFrom >= 0)
            for (var i = _nearFrom; i <= _nearTo && i < _entries.Count; i++)
                if (i < from || i > to) _entries[i].NearPlayhead = false;
        for (var i = from; i <= to && i < _entries.Count; i++) _entries[i].NearPlayhead = true;
        _nearFrom = from; _nearTo = to;
    }

    private void UpdateLogPlayhead(bool force = false)
    {
        _ = force;
        if (LogPlayheadLine == null || GroupsList == null) return;
        if (_groups.Count == 0 || EntriesScroller.Content is not Visual content)
        {
            LogPlayheadLine.IsVisible = false;
            _logLineContentY = null;
            return;
        }

        double t = _lastPlayhead;

        // Data-level scan (cheap): active = last anchor with time <= t; next = first with time > t.
        // Anchors in display order: each group header (time = clip start), then its entries (if expanded).
        object? activeItem = null, nextItem = null;
        foreach (var g in _groups)
        {
            if (g.Start <= t) activeItem = g;
            else if (nextItem == null) nextItem = g;
            if (nextItem != null) break;
            if (g.IsExpanded)
            {
                foreach (var e in g.Entries)
                {
                    if (e.T <= t) activeItem = e;
                    else if (nextItem == null) nextItem = e;
                    if (nextItem != null) break;
                }
                if (nextItem != null) break;
            }
        }

        // One visual-tree pass: map each realized header/row to its container (O(1) lookups after).
        var map = new Dictionary<object, Control>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<Visual>();
        queue.Enqueue(GroupsList);
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (v is ContentPresenter cp && cp.DataContext is { } dc && (dc is ClipGroup || dc is LogEntry))
                map[dc] = cp;
            foreach (var child in v.GetVisualChildren()) queue.Enqueue(child);
        }

        double? ContentTop(object? item) =>
            item != null && map.TryGetValue(item, out var c) && c.TranslatePoint(new Point(0, 0), content) is { } p
                ? p.Y : (double?)null;
        double? ContentBottom(object? item) =>
            item != null && map.TryGetValue(item, out var c) && c.TranslatePoint(new Point(0, 0), content) is { } p
                ? p.Y + c.Bounds.Height : (double?)null;

        // Line position: top of the next anchor; if none (past everything), bottom of the active row.
        _logLineContentY = ContentTop(nextItem) ?? ContentBottom(activeItem)
            ?? (activeItem == null && nextItem == null ? 0 : (double?)null);

        // Active-anchor centre, for lock-to-centre. A log row uses its exact centre (matches
        // ScrollEntryIntoCenter, so adding a log doesn't jump). A clip header wraps all its entries, so
        // don't use its geometric centre — fall back to the line position (top of its first entry).
        if (activeItem is LogEntry && map.TryGetValue(activeItem, out var ec) &&
            ec.TranslatePoint(new Point(0, 0), content) is { } ep)
            _logActiveCenterContentY = ep.Y + ec.Bounds.Height / 2;
        else if (_logLineContentY is { } ly)
            _logActiveCenterContentY = ly;
        else if (ContentTop(activeItem) is { } at)
            _logActiveCenterContentY = at;

        PositionLogPlayhead();
    }

    private void PositionLogPlayhead()
    {
        if (_logLineContentY is not { } cy) { LogPlayheadLine.IsVisible = false; return; }
        double viewportH = EntriesScroller.Viewport.Height;
        if (viewportH <= 0) { LogPlayheadLine.IsVisible = false; return; }

        // Lock-to-centre recenters whenever the PLAYHEAD MOVES — playing, OR seeking/scrubbing while
        // paused (matching the video/transcript centre-locks). It does NOT fight a paused user who is
        // just manually scrolling the list (playhead unchanged → no snap). The NaN sentinel forces an
        // immediate centre when the lock is first switched on.
        var playheadMoved = double.IsNaN(_logLastCenteredPlayhead)
                            || Math.Abs(_lastPlayhead - _logLastCenteredPlayhead) > 0.02;
        if (_logLockCenter && (IsPlaying() || playheadMoved))
        {
            double maxScroll = Math.Max(0, EntriesScroller.Extent.Height - viewportH);
            double targetScroll = Math.Clamp(_logActiveCenterContentY - viewportH / 2, 0, maxScroll);
            if (Math.Abs(EntriesScroller.Offset.Y - targetScroll) > 0.5)
            {
                // Setting Offset re-raises ScrollChanged synchronously; the guard stops the handler from
                // re-entering this method (matching the _applyingRestore pattern for the timelines).
                _adjustingLogScroll = true;
                EntriesScroller.Offset = new Vector(EntriesScroller.Offset.X, targetScroll);
                _adjustingLogScroll = false;
            }
            _logLastCenteredPlayhead = _lastPlayhead;
        }

        double y = cy - EntriesScroller.Offset.Y;
        if (y < 0 || y > viewportH) { LogPlayheadLine.IsVisible = false; return; }
        // +2 accounts for the scroller's top margin (it sits inside the same Grid as the line).
        LogPlayheadLine.Margin = new Thickness(8, 2 + y, 6, 0);
        LogPlayheadLine.IsVisible = true;
    }

    // Scroll the manual-log list so the given entry sits in the vertical centre of the viewport.
    private void ScrollEntryIntoCenter(LogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FindEntryContainer(entry) is not { } target) return;
            if (EntriesScroller.Content is not Visual content) return;
            if (target.TranslatePoint(new Point(0, 0), content) is not { } p) return;
            var center = p.Y + target.Bounds.Height / 2;
            var max = Math.Max(0, EntriesScroller.Extent.Height - EntriesScroller.Viewport.Height);
            var y = Math.Clamp(center - EntriesScroller.Viewport.Height / 2, 0, max);
            // Never push a non-finite offset: before layout has settled Extent/Viewport can be NaN, and a NaN
            // scroll offset throws deep inside Avalonia's layout pass (an unrecoverable, app-killing crash).
            if (double.IsNaN(y) || double.IsInfinity(y)) return;
            EntriesScroller.Offset = new Vector(EntriesScroller.Offset.X, y);
        }, DispatcherPriority.Loaded);
    }

    // Focus the editable text box of an entry that just entered edit mode, caret at end.
    private void FocusEntryEditor(LogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FindEntryContainer(entry) is not { } container) return;
            foreach (var d in container.GetVisualDescendants())
                if (d is TextBox tb && (tb.Tag as string) == "editText")
                {
                    tb.Focus();
                    tb.CaretIndex = tb.Text?.Length ?? 0;
                    break;
                }
        }, DispatcherPriority.Loaded);
    }

    private void OnDeleteEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LogEntry le })
        {
            _entries.Remove(le);
            // Incremental removal: pull the row out of its clip group instead of clearing + rebuilding every
            // group (the log list isn't virtualised, so a full rebuild re-creates thousands of controls — that
            // was the delete lag).
            var removed = false;
            foreach (var g in _groups) if (g.Entries.Remove(le)) { removed = true; break; }
            if (removed) { UpdateTimelineMarkers(); RefreshSearchIfActive(); }
            else RebuildGroupsAndMarkers(); // not found (shouldn't happen) → fall back to the full rebuild
            MarkDirty();
        }
    }

    // ---- Merge / split logs ------------------------------------------------

    // Original (time,text) components of an entry: its stored parts, or itself if it was never merged.
    private static List<MergePart> PartsOf(LogEntry e) =>
        e.MergedParts is { Count: > 0 } p
            ? p.Select(x => new MergePart { T = x.T, Text = x.Text }).ToList()
            : new List<MergePart> { new() { T = e.T, Text = e.Text } };

    // Open the merge dropdown (up / down) anchored to the row's ⇅ button, capturing its entry directly.
    private void OnMergeMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LogEntry le } b) return;
        var menu = new ContextMenu();
        var up = new MenuItem { Header = "▲ Merge up" };
        up.Click += (_, _) => MergeAdjacentLog(le, -1);
        var down = new MenuItem { Header = "▼ Merge down" };
        down.Click += (_, _) => MergeAdjacentLog(le, +1);
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Open(b);
    }

    // Merge an entry with its neighbour above (-1) or below (+1) in time order.
    private void MergeAdjacentLog(LogEntry le, int dir)
    {
        var ordered = _entries.OrderBy(x => x.T).ToList();
        var idx = ordered.IndexOf(le);
        if (idx < 0) return;
        var ni = idx + dir;
        if (ni < 0 || ni >= ordered.Count) return;
        var other = ordered[ni];

        var parts = PartsOf(le); parts.AddRange(PartsOf(other));
        parts.Sort((a, b) => a.T.CompareTo(b.T));
        var t = Math.Min(le.T, other.T);
        var merged = new LogEntry
        {
            T = t, OffsetSeconds = OffsetAt(t), MergedParts = parts,
            Text = string.Join("\n", parts.Select(p => p.Text).Where(x => !string.IsNullOrWhiteSpace(x))),
            Color = le.Color != LogEntry.DefaultColor ? le.Color : other.Color,
            Copied = le.Copied || other.Copied, // don't silently reset the copied/struck state
        };
        _entries.Remove(le);
        _entries.Remove(other);
        var i = 0; while (i < _entries.Count && _entries[i].T <= merged.T) i++;
        _entries.Insert(i, merged);
        RebuildGroupsAndMarkers();
        MarkDirty();
        ScrollEntryIntoCenter(merged);
    }

    // Split a merged entry back into its original components.
    private void OnSplitEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LogEntry le }) return;
        if (le.MergedParts is not { Count: > 1 } parts) return;
        _entries.Remove(le);
        foreach (var p in parts)
        {
            var ne = new LogEntry { T = p.T, OffsetSeconds = OffsetAt(p.T), Text = p.Text, Color = le.Color };
            var i = 0; while (i < _entries.Count && _entries[i].T <= ne.T) i++;
            _entries.Insert(i, ne);
        }
        RebuildGroupsAndMarkers();
        MarkDirty();
    }

    private void OnCancelEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LogEntry le })
        {
            le.IsEditing = false;
            if (ReferenceEquals(le, _editResumeEntry)) _editResumeEntry = null; // cancel ≠ resume
        }
    }

    private void OnCommitEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is LogEntry le)
            CommitEntryEdit(c, le);
    }

    // Enter inside an edit-row TextBox = same as clicking the ✓ button.
    private void OnEditTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is Control c && c.DataContext is LogEntry le)
        {
            CommitEntryEdit(c, le);
            e.Handled = true;
        }
    }

    private void CommitEntryEdit(Control source, LogEntry le)
    {
        // Find the matching edit-row TextBoxes and apply.
        if (FindRowTextBox(source, "editTime") is { } tbTime &&
            TryParseTimecode(tbTime.Text, out var displayed))
        {
            // displayed = T + offset, so canonical T = displayed - offset. Use this entry's own
            // sync-point offset (edits normally stay within the same segment); then recompute it in
            // case the new time crossed a sync point.
            le.T = Math.Max(0, displayed - le.OffsetSeconds);
            le.OffsetSeconds = OffsetAt(le.T);
        }
        // Text is already two-way bound on the edit TextBox; nothing else to copy.
        // The edit makes the combined text authoritative — drop the stale merge parts (and the split button)
        // so a later Split can't revert the edit.
        le.MergedParts = null;
        le.IsEditing = false;
        // Re-sort + regroup (insertion is by T, then sorted within group).
        var idx = _entries.IndexOf(le);
        if (idx >= 0)
        {
            _entries.RemoveAt(idx);
            var i = 0;
            while (i < _entries.Count && _entries[i].T <= le.T) i++;
            _entries.Insert(i, le);
        }
        // Incremental regroup rather than RebuildGroupsAndMarkers(): the full rebuild re-creates the entire
        // non-virtualised log list, which is what made committing an edit hang for about a second.
        var target = FindGroupForTime(le.T);
        if (target == null) RebuildGroupsAndMarkers(); // moved outside every clip → full rebuild (rare)
        else
        {
            foreach (var g in _groups) if (g.Entries.Remove(le)) break;
            var ins = 0;
            while (ins < target.Entries.Count && target.Entries[ins].T <= le.T) ins++;
            target.Entries.Insert(ins, le);
            target.IsExpanded = true;
            UpdateTimelineMarkers();
            RefreshSearchIfActive();
        }
        MarkDirty();
        // If this edit was opened via "E", resume playback iff the video was playing when E paused it.
        if (ReferenceEquals(le, _editResumeEntry))
        {
            if (_editResumeWasPlaying) try { _mpv?.Pause.Set(false); } catch { /* unavailable */ }
            _editResumeEntry = null;
        }
    }
    private static TextBox? FindRowTextBox(object? sender, string tag)
    {
        if (sender is not Control c) return null;
        var p = c.Parent;
        while (p is not null and not Border) p = p.Parent;
        if (p is not Border border) return null;
        return FindByTag(border, tag) as TextBox;
    }

    private static Control? FindByTag(Control root, string tag)
    {
        if (root.Tag is string s && s == tag) return root;
        if (root is Panel panel)
            foreach (var child in panel.Children)
                if (child is Control cc && FindByTag(cc, tag) is { } hit) return hit;
        if (root is Decorator d && d.Child is Control dc && FindByTag(dc, tag) is { } dhit) return dhit;
        if (root is ContentControl cp && cp.Content is Control ccp && FindByTag(ccp, tag) is { } cphit) return cphit;
        return null;
    }

    private async System.Threading.Tasks.Task ResetCopyAllLabel()
    {
        await System.Threading.Tasks.Task.Delay(900);
        CopyAllBtn.Content = "Copy all";
    }

    private async System.Threading.Tasks.Task SetClipboardAsync(string text)
    {
        var cb = GetTopLevel(this)?.Clipboard;
        if (cb != null) await cb.SetTextAsync(text);
    }

    // ---- Project save / open ----------------------------------------------

    private sealed class DayMarkerDto
    {
        public string SourcePath { get; set; } = "";
        public string Text { get; set; } = "";
        public string Color { get; set; } = DayMarker.DefaultColor;
    }

    private sealed class ProjectDto
    {
        public int Version { get; set; } = 2; // v2 adds embedded thumbnails, waveforms, transcript
        public List<string> Sources { get; set; } = new();
        public double TimerOffsetSeconds { get; set; }
        // Re-sync points: parallel arrays (video time → on-screen timer reading), sorted by video time.
        public double[]? OffsetAnchorVideo { get; set; }
        public double[]? OffsetAnchorTimer { get; set; }
        public int SpeedIndex { get; set; } = 2;       // legacy: preset index (read only when Speed is 0)
        public double Speed { get; set; }              // actual playback speed (free value); 0 = use SpeedIndex
        public double PlayheadSeconds { get; set; }
        public double SentenceTargetSec { get; set; } = 8;   // now: silence-pause seconds
        public double SilenceThreshold { get; set; } = 0.05; // legacy single value (pre per-track)
        public double RowSizeScale { get; set; } = 1.0;
        public List<EntryDto> Entries { get; set; } = new();

        // Per-track silence segmentation (index aligns with TrackTitles).
        public List<double> TrackPause { get; set; } = new();
        public List<double> TrackLevel { get; set; } = new();
        public List<double> TrackVolume { get; set; } = new();

        // View state — both timelines' zoom/scroll + the lock toggles.
        public double VideoPps { get; set; }
        public double VideoScroll { get; set; }
        public double TranscriptPps { get; set; }
        public double TranscriptScroll { get; set; }
        public bool VideoCenterLock { get; set; }
        public bool TranscriptCenterLock { get; set; }
        public bool ManualLogCenterLock { get; set; }
        public bool LockZoom { get; set; }
        // Transcript track columns: relative widths + per-track "hidden entirely" (index aligns with tracks).
        public List<double> TranscriptTrackWeights { get; set; } = new();
        public List<bool> TranscriptTrackCollapsed { get; set; } = new();
        public bool AutoLevel { get; set; }              // legacy (global) — read for back-compat only
        public bool[]? AutoLevelTracks { get; set; }     // per-track auto-level enable
        public double AutoLevelTarget { get; set; } = 0.9;

        // Per-clip filmstrip: jpg bytes base64-encoded so the project file is a portable single file.
        // Index aligns with Sources.
        public List<ClipAssetsDto> ClipAssets { get; set; } = new();
        // Chapter dividers from the import folders. Absent in projects saved before day markers
        // existed, which deserializes to an empty list — older builds ignore the field.
        public List<DayMarkerDto> DayMarkers { get; set; } = new();

        // Per-track raw transcript segments, timeline-absolute seconds.
        public List<List<SegmentDto>> Transcript { get; set; } = new();
        public List<string> TrackTitles { get; set; } = new();
    }

    private sealed class EntryDto
    {
        public string Id { get; set; } = "";
        public double T { get; set; }
        public string Text { get; set; } = "";
        public string Source { get; set; } = "manual";
        public string CreatedAtUtc { get; set; } = "";
        public string Color { get; set; } = "#FFCB5C";
        public bool Copied { get; set; }
        public List<MergePartDto>? MergedParts { get; set; } // present only for merged entries
    }

    private sealed class MergePartDto { public double T { get; set; } public string Text { get; set; } = ""; }

    private sealed class ClipAssetsDto
    {
        public List<string> ThumbnailsB64 { get; set; } = new();  // JPEG bytes per frame
        public List<double> ThumbnailTimes { get; set; } = new(); // clip-relative seconds
        // Per audio track: peak buckets as 8-bit (0–255) bytes, base64-encoded. One byte per peak
        // instead of a 4-byte float — a 4× saving, imperceptible for a draw-only envelope.
        public List<string?> WaveU8 { get; set; } = new();
        // Legacy float32-base64 waveforms. Still READ for old projects; no longer written.
        public List<string?> WaveformsB64 { get; set; } = new();
    }

    private sealed class SegmentDto
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = "";
        public string Speaker { get; set; } = "";
    }

    // includeAssets=false omits the heavy embedded thumbnails/waveforms — used for the lightweight
    // rotating backup snapshots so they stay small (the data worth preserving is the logs/transcript).
    private ProjectDto BuildProjectDto(bool includeAssets = true)
    {
        var dto = new ProjectDto
        {
            Sources = (_currentSources ?? new List<string>()).Select(CanonicalFor).ToList(), // never this machine's paths
            TimerOffsetSeconds = _timerOffset,
            OffsetAnchorVideo = _offsetAnchors.Select(a => a.VideoTime).ToArray(),
            OffsetAnchorTimer = _offsetAnchors.Select(a => a.TimerValue).ToArray(),
            SpeedIndex = SpeedBox.SelectedIndex,
            Speed = _speed,
            PlayheadSeconds = CurrentTime(),
            SentenceTargetSec = _trackPause.Length > 0 ? _trackPause[0] : DefaultPauseSec, // legacy
            SilenceThreshold = _trackLevel.Length > 0 ? _trackLevel[0] : DefaultLevel,     // legacy
            RowSizeScale = 1.0, // legacy field, no longer drives anything
            TrackPause = _trackPause.ToList(),
            TrackVolume = _volumes.ToList(),
            TrackLevel = _trackLevel.ToList(),
            VideoPps = Timeline.PixelsPerSecond,
            VideoScroll = Timeline.ScrollSeconds,
            TranscriptPps = TranscriptTimeline.PixelsPerSecond,
            TranscriptScroll = TranscriptTimeline.ScrollSeconds,
            VideoCenterLock = VideoCenterLockBtn.IsChecked == true,
            TranscriptCenterLock = TranscriptCenterLockBtn.IsChecked == true,
            ManualLogCenterLock = LogCenterLockBtn.IsChecked == true,
            LockZoom = ScrollLockBtn.IsChecked == true,
            TranscriptTrackWeights = TranscriptTimeline.GetTrackWeights().ToList(),
            TranscriptTrackCollapsed = TranscriptTimeline.GetTrackCollapsed().ToList(),
            AutoLevelTracks = (bool[])_autoLevelTrack.Clone(),
            AutoLevelTarget = _autoLevelTarget,
            Entries = _entries.OrderBy(e => e.T).Select(e => new EntryDto
            {
                Id = e.Id, T = e.T, Text = e.Text, Source = e.Source, Color = e.Color, Copied = e.Copied,
                CreatedAtUtc = e.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                MergedParts = e.MergedParts?.Select(p => new MergePartDto { T = p.T, Text = p.Text }).ToList(),
            }).ToList(),
            TrackTitles = _audioTitles.ToList(),
        };

        // Asset base64 encoding (thumbnails + waveforms) is the expensive part and the assets don't change
        // when you just add/edit/delete a log — so cache the encoded list and only rebuild it when the
        // assets actually changed (extraction / load / reset set _assetsDirty). This keeps per-log saves fast.
        if (includeAssets)
        {
            if (_assetsDirty || _cachedClipAssets == null)
            {
                var list = new List<ClipAssetsDto>();
                var n = _currentSources?.Count ?? 0;
                for (var c = 0; c < n; c++)
                {
                    var a = new ClipAssetsDto();
                    if (_clipThumbnailJpegs != null && c < _clipThumbnailJpegs.Length && _clipThumbnailJpegs[c] != null)
                        foreach (var b in _clipThumbnailJpegs[c]) a.ThumbnailsB64.Add(Convert.ToBase64String(b));
                    if (_clipThumbnailTimes != null && c < _clipThumbnailTimes.Length && _clipThumbnailTimes[c] != null)
                        a.ThumbnailTimes = _clipThumbnailTimes[c].ToList();
                    if (_clipWaveforms != null && c < _clipWaveforms.Length && _clipWaveforms[c] != null)
                    {
                        foreach (var peaks in _clipWaveforms[c])
                        {
                            if (peaks == null) { a.WaveU8.Add(null); continue; }
                            var bytes = new byte[peaks.Length];
                            for (var p = 0; p < peaks.Length; p++)
                                bytes[p] = (byte)Math.Clamp((int)MathF.Round(peaks[p] * 255f), 0, 255);
                            a.WaveU8.Add(Convert.ToBase64String(bytes));
                        }
                    }
                    list.Add(a);
                }
                _cachedClipAssets = list;
                _assetsDirty = false;
            }
            dto.ClipAssets = _cachedClipAssets;
        }

        dto.DayMarkers = _dayMarkers
            .Select(m => new DayMarkerDto { SourcePath = CanonicalFor(m.SourcePath), Text = m.Text, Color = m.Color })
            .ToList();

        foreach (var raw in _rawSegmentsByTrack)
            dto.Transcript.Add(raw.Select(s => new SegmentDto
            {
                Start = s.Start, End = s.End, Text = s.Text, Speaker = s.Speaker,
            }).ToList());

        return dto;
    }

    // Project files are gzip-compressed JSON. SmallestSize recovers the base64 overhead on the
    // embedded JPEGs and crushes the long silent runs in the waveforms. leaveOpen so the caller
    // still owns the destination stream.
    private static async System.Threading.Tasks.Task SerializeProjectAsync(Stream dest, ProjectDto dto)
    {
        // Optimal (deflate level 6) instead of SmallestSize (level 9): ~3-5× faster to write with a
        // negligible size delta here, because the bulk is already-compressed JPEG bytes that gzip can't
        // shrink much further. Lossless either way.
        await using var gz = new GZipStream(dest, CompressionLevel.Optimal, leaveOpen: true);
        await JsonSerializer.SerializeAsync(gz, dto, JsonOpts);
        await gz.FlushAsync();
    }

    // Write the project to disk safely: serialize (off the UI thread) to a temp file, then atomically
    // move it over the target. The real file is never half-written, so a crash mid-save can't corrupt
    // an existing project.
    private static async System.Threading.Tasks.Task WriteProjectFileAsync(string path, ProjectDto dto)
    {
        var tmp = path + ".saving.tmp";
        try
        {
            await System.Threading.Tasks.Task.Run(async () =>
            {
                await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                                    1 << 16, useAsync: true);
                await SerializeProjectAsync(fs, dto);
            });
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    // Reads either the new gzip format (magic 0x1F 0x8B) or a legacy plain-JSON project file.
    private static async System.Threading.Tasks.Task<ProjectDto?> DeserializeProjectAsync(Stream src)
    {
        using var ms = new MemoryStream();
        await src.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) return null;

        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var input = new MemoryStream(bytes);
            await using var gz = new GZipStream(input, CompressionMode.Decompress);
            return await JsonSerializer.DeserializeAsync<ProjectDto>(gz, JsonOpts);
        }
        using var plain = new MemoryStream(bytes);
        return await JsonSerializer.DeserializeAsync<ProjectDto>(plain, JsonOpts);
    }

    // Ctrl+S / Save: overwrite the current project if there is one; otherwise prompt for a location.
    // silent = background save (auto-save, save-after-log): updates the status text but no big toast.
    private async System.Threading.Tasks.Task SaveProjectAsync(bool forceDialog, bool silent = false)
    {
        // An empty project (no footage yet) is now valid — it just needs a save location. Only block when
        // there's truly nothing to save AND nowhere to save it AND the user didn't explicitly pick Save As.
        if ((_currentSources == null || _currentSources.Count == 0)
            && string.IsNullOrEmpty(_currentProjectPath) && !forceDialog)
        {
            ClipInfo.Text = "nothing to save — create a project first";
            return;
        }

        // NB: the "Saving…" toast is deliberately NOT shown yet. It is a centred, non-dismissable
        // popup, so showing it before the save picker leaves it sitting on top of the dialog for as
        // long as the user is browsing folders — and nothing is being saved during that time anyway.
        var path = forceDialog ? null : _currentProjectPath;
        if (path == null)
        {
            var top = GetTopLevel(this);
            if (top == null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save project",
                SuggestedFileName = "review.frproj",
                DefaultExtension = "frproj",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Footage Reviewer project") { Patterns = new[] { "*.frproj" } },
                },
            });
            if (file == null) return; // picker cancelled
            path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                // non-filesystem target — write through the storage stream and don't track a path
                try
                {
                    if (!silent) ShowSavingToast();
                    await using var stream = await file.OpenWriteAsync();
                    await SerializeProjectAsync(stream, BuildProjectDto());
                    _currentProjectPath = null;
                    ClearDirty();
                    OnSavedNow();
                    SetProjectTitle(file.Name);
                    ClipInfo.Text = $"saved · {file.Name}";
                    if (!silent) ShowSavedToast();
                }
                catch (Exception ex) { HideSaveToast(); ClipInfo.Text = "save failed: " + ex.Message; }
                return;
            }
        }

        try
        {
            if (!silent) ShowSavingToast();
            // Build the DTO on the UI thread, then compress + write atomically off-thread.
            await WriteProjectFileAsync(path, BuildProjectDto());
            _currentProjectPath = path;
            RememberLastProject(path);
            ClearDirty();
            OnSavedNow();
            SetProjectTitle(Path.GetFileName(path));
            ClipInfo.Text = $"saved · {Path.GetFileName(path)}";
            if (!silent) ShowSavedToast();
        }
        catch (Exception ex)
        {
            HideSaveToast();
            ClipInfo.Text = "save failed: " + ex.Message;
        }
    }

    private void SetProjectTitle(string name) => Title = $"Footage Reviewer — {name}{(_dirty ? " •" : "")}";

    // Remember the most recent project file so it auto-opens next launch.
    private void RememberLastProject(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        // Maintain the most-recently-used list: move-to-front, de-duped, capped. Deliberately does NOT drop
        // entries whose file is currently unreachable - with the projects living on a NAS, opening anything
        // while it was offline used to silently erase the rest of the history. The launcher shows those
        // greyed out instead, and they can be removed explicitly.
        var recent = _settings.RecentProjects ??= new();
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        if (recent.Count > _settings.MaxRecentProjects)
            recent.RemoveRange(_settings.MaxRecentProjects, recent.Count - _settings.MaxRecentProjects);
        ProjectIndex.TouchOpened(path); // drives "last opened" on the launcher card
        _settings.LastProjectPath = path;
        _settings.Save();
    }

    // File → Rename project: flush the current state, then rename the .frproj on disk (and its .backups
    // folder) and re-point the project + recent list at the new path.
    private async Task RenameProjectAsync()
    {
        if (string.IsNullOrEmpty(_currentProjectPath)) { ClipInfo.Text = "save the project first, then rename it"; return; }
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit…"; return; }

        var oldPath = _currentProjectPath!;
        var dir = Path.GetDirectoryName(oldPath) ?? "";
        var oldStem = Path.GetFileNameWithoutExtension(oldPath);

        var newName = await new RenameProjectWindow(oldStem).ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(newName)) return;
        newName = newName.Trim();
        if (string.Equals(newName, oldStem, StringComparison.Ordinal)) return; // unchanged

        var newPath = Path.Combine(dir, newName + ".frproj");
        if (File.Exists(newPath)) { ClipInfo.Text = "a project with that name already exists here"; return; }

        // Flush the latest content to the OLD file first so the rename carries everything.
        await SaveProjectAsync(forceDialog: false, silent: true);
        try
        {
            if (File.Exists(oldPath)) File.Move(oldPath, newPath);
            // Keep the rotating backups associated with the project.
            var oldBackups = Path.Combine(dir, oldStem + ".backups");
            var newBackups = Path.Combine(dir, newName + ".backups");
            if (Directory.Exists(oldBackups) && !Directory.Exists(newBackups)) Directory.Move(oldBackups, newBackups);
        }
        catch (Exception ex)
        {
            ClipInfo.Text = "rename failed: " + ex.Message;
            DiagnosticsLogger.LogException("RenameProjectAsync", ex);
            return;
        }

        _currentProjectPath = newPath;
        SetProjectTitle(Path.GetFileName(newPath));
        _settings.RecentProjects?.RemoveAll(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
        RememberLastProject(newPath);
        ClipInfo.Text = $"renamed to {newName}.frproj";
    }

    // _changeSeq bumps on every CONTENT change (logs, transcript, offsets, settings) — the versioned
    // backup uses it to skip making snapshots when nothing meaningful changed. Viewport-only changes
    // (zoom/pan) still set _dirty so a save captures the new zoom, but don't bump _changeSeq.
    private long _changeSeq;
    private long _savedContentSeq;   // _changeSeq at the last save — drives the visible "unsaved" indicator
    // Only CONTENT changes (logs, transcript, offsets, marker colours…) count as "unsaved" to the user.
    // Viewport/playhead changes still get saved, but don't light up the indicator or the title dot.
    private bool ContentDirty => _changeSeq != _savedContentSeq;

    private void MarkDirty(bool contentChanged = true)
    {
        _dirty = true;
        if (contentChanged) _changeSeq++;
        UpdateTitleDot();
        UpdateSaveStatus();
    }

    private void UpdateTitleDot()
    {
        if (!string.IsNullOrEmpty(_currentProjectPath))
            Title = $"Footage Reviewer — {Path.GetFileName(_currentProjectPath)}{(ContentDirty ? " •" : "")}";
    }

    private void ClearDirty()
    {
        _dirty = false;
        _savedContentSeq = _changeSeq;
        UpdateTitleDot();
        UpdateSaveStatus();
    }

    private void OnSavedNow()
    {
        _lastSavedUtc = DateTime.UtcNow;
        UpdateSaveStatus();
    }

    private DispatcherTimer? _saveToastTimer;
    private bool _toastFading;

    // Shown immediately when an explicit save starts; stays up (no timer) until ShowSavedToast.
    private void ShowSavingToast()
    {
        _saveToastTimer?.Stop();
        _toastFading = false;
        SaveToastIcon.Text = "💾";
        SaveToastText.Text = "Saving…";
        SaveToastBorder.Opacity = 1;
        SaveToastOverlay.IsVisible = true; // in-window overlay (the video is Avalonia-composited now)
    }

    // Replaces "Saving…" with "Saved", lingers 2s, then fades out.
    private void ShowSavedToast()
    {
        _toastFading = false;
        SaveToastIcon.Text = "💾";
        SaveToastText.Text = "Saved";
        SaveToastBorder.Opacity = 1;
        SaveToastOverlay.IsVisible = true;
        _saveToastTimer ??= new DispatcherTimer();
        _saveToastTimer.Tick -= OnSaveToastTick;
        _saveToastTimer.Tick += OnSaveToastTick;
        _saveToastTimer.Stop();
        _saveToastTimer.Interval = TimeSpan.FromSeconds(2);
        _saveToastTimer.Start();
    }

    private void HideSaveToast()
    {
        _saveToastTimer?.Stop();
        _toastFading = false;
        SaveToastOverlay.IsVisible = false;
    }

    private void OnSaveToastTick(object? s, EventArgs e)
    {
        if (!_toastFading)
        {
            // Linger elapsed → start the opacity fade (DoubleTransition on the border), tick again soon.
            _toastFading = true;
            SaveToastBorder.Opacity = 0;
            if (_saveToastTimer != null) _saveToastTimer.Interval = TimeSpan.FromMilliseconds(450);
        }
        else
        {
            _toastFading = false;
            _saveToastTimer?.Stop();
            SaveToastOverlay.IsVisible = false;
        }
    }

    private void UpdateSkipPreview()
        => Timeline.SetSkipPreview(EffectiveSkip(), SkipPreviewBtn.IsChecked == true);

    private void UpdateSaveStatus()
    {
        if (SaveStatus == null) return;
        if (_lastSavedUtc is not DateTime t)
        {
            SaveStatus.Text = ContentDirty ? "Unsaved" : "";
            SaveStatus.Foreground = Brush.Parse(ContentDirty ? "#FFCB5C" : "#7E7E8C");
            return;
        }
        var ago = DateTime.UtcNow - t;
        var label = ago.TotalSeconds < 5 ? "Saved · just now"
            : ago.TotalSeconds < 60 ? $"Saved · {(int)ago.TotalSeconds}s ago"
            : ago.TotalMinutes < 60 ? $"Saved · {(int)ago.TotalMinutes}m ago"
            : $"Saved · {(int)ago.TotalHours}h ago";
        if (ContentDirty) label += " · unsaved changes";
        SaveStatus.Text = label;
        SaveStatus.Foreground = Brush.Parse(ContentDirty ? "#FFCB5C" : "#8CE6A0");
    }

    private async System.Threading.Tasks.Task OpenProjectAsync()
    {
        var top = GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Footage Reviewer project") { Patterns = new[] { "*.frproj" } },
                FilePickerFileTypes.All,
            },
        });
        var f = files.FirstOrDefault();
        if (f == null) return;

        ProjectDto? dto;
        try
        {
            await using var stream = await f.OpenReadAsync();
            dto = await DeserializeProjectAsync(stream);
        }
        catch (Exception ex)
        {
            ClipInfo.Text = "open failed: " + ex.Message;
            return;
        }
        if (dto == null) return;
        await ResolveMissingSourcesAsync(dto); // footage moved? offer to relink before loading
        ApplyOpenedProject(dto, f.TryGetLocalPath());
    }

    // File → Open recent: rebuild the submenu from the MRU list each time it opens (so deleted files drop
    // out). The submenu opens on hover once the File menu is open — Premiere-style.
    private void RefreshRecentProjectsMenu()
    {
        MenuOpenRecent.Items.Clear();
        var recent = (_settings.RecentProjects ?? new()).ToList();
        if (recent.Count == 0)
        {
            MenuOpenRecent.Items.Add(new MenuItem { Header = "(no recent projects)", IsEnabled = false });
            return;
        }
        foreach (var path in recent)
        {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path) };
            ToolTip.SetTip(item, path); // full path on hover, so two same-named projects are distinguishable
            var captured = path;
            item.Click += async (_, _) => await OpenProjectFromPathAsync(captured);
            MenuOpenRecent.Items.Add(item);
        }
        MenuOpenRecent.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear recent" };
        clear.Click += (_, _) => { _settings.RecentProjects?.Clear(); _settings.Save(); };
        MenuOpenRecent.Items.Add(clear);
    }

    // Open a specific project file (from the Open-recent menu). Mirrors the file-picker open path.
    private async System.Threading.Tasks.Task OpenProjectFromPathAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            ClipInfo.Text = $"project not found: {Path.GetFileName(path)}";
            _settings.RecentProjects?.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            return;
        }
        ProjectDto? dto;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            dto = await DeserializeProjectAsync(fs);
        }
        catch (Exception ex) { ClipInfo.Text = "open failed: " + ex.Message; return; }
        if (dto == null) { DiagnosticsLogger.Log("reopen last project: deserialize returned null"); return; }
        // ApplyOpenedProject runs on a detached task here; without this catch a fault leaves the app
        // silently showing "No footage loaded" with nothing logged.
        await ResolveMissingSourcesAsync(dto); // footage moved? offer to relink before loading
        try { ApplyOpenedProject(dto, path); }
        catch (Exception ex) { DiagnosticsLogger.LogException("reopen last project: apply", ex); }
    }

    // Auto-open the most recent project on launch. If the file is gone, carry on with an empty project
    // and report where it used to live.
    private async System.Threading.Tasks.Task TryOpenLastProjectAsync()
    {
        var path = _settings.LastProjectPath;
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path))
        {
            ClipInfo.Text = $"Last project couldn't be found: {Path.GetFileName(path)}  ·  last known folder: {Path.GetDirectoryName(path)}";
            return;
        }
        ProjectDto? dto;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            dto = await DeserializeProjectAsync(fs);
        }
        catch (Exception ex) { ClipInfo.Text = "couldn't reopen last project: " + ex.Message; return; }
        if (dto == null) return;
        await ResolveMissingSourcesAsync(dto); // footage moved? offer to relink before loading
        ApplyOpenedProject(dto, path);
    }

    // Apply a deserialized project + its on-disk path. Shared by the file picker and the launch auto-open.
    // ---- missing-footage relink -------------------------------------------------------------------

    /// <summary>
    /// Called before a project's media is loaded. If any of its recordings no longer resolve (almost always
    /// because the footage folder was moved or renamed) offer to repoint the project at the new folder, drop
    /// the dead clips, or carry on. Mutates <paramref name="dto"/> in place; the project is only written back
    /// if the user saves, so declining changes nothing on disk.
    /// </summary>
    private async Task ResolveMissingSourcesAsync(ProjectDto dto)
    {
        ApplyMediaLocations(dto); // this machine's copy first; only what's still missing gets a dialog
        while (true)
        {
            var missing = dto.Sources
                .Select((p, i) => (Path: p, Index: i))
                .Where(x => !string.IsNullOrEmpty(x.Path) && !x.Path.StartsWith("av://") && !File.Exists(x.Path))
                .ToList();
            if (missing.Count == 0) return;

            // A missing entry whose filename is ALREADY present under a working path is a leftover from a
            // move — dropping it loses nothing (this is the "half-relinked, everything duplicated" case).
            var live = new HashSet<string>(
                dto.Sources.Where(File.Exists).Select(p => PathText.FileName(p)), StringComparer.OrdinalIgnoreCase);
            var dupes = missing.Count(m => live.Contains(PathText.FileName(m.Path)));

            var dlg = new RelinkFootageWindow(missing.Select(m => m.Path).ToList(), dto.Sources.Count, dupes);
            var choice = await dlg.ShowDialog<RelinkChoice?>(this);

            if (choice is null or RelinkChoice.OpenAnyway)
            {
                ClipInfo.Text = $"{missing.Count} clip(s) missing — footage not loaded for them";
                return;
            }

            if (choice == RelinkChoice.RemoveMissing)
            {
                DropSourcesAt(dto, missing.Select(m => m.Index).ToHashSet());
                ClipInfo.Text = $"removed {missing.Count} missing clip(s)";
                return;
            }

            // Locate: pick a folder and match the missing filenames inside it (recursively).
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Where is the footage now?",
                AllowMultiple = false,
            });
            var root = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(root)) continue; // cancelled the picker → back to the dialog

            var found = await Task.Run(() => IndexByFileName(root!));
            var fixedCount = 0;
            var pairs = new List<(string Old, string New)>();
            foreach (var m in missing)
            {
                // The project may have been written on the other OS, so split on either separator.
                var name = PathText.FileName(m.Path);
                if (name.Length == 0 || !found.TryGetValue(name, out var full)) continue;
                dto.Sources[m.Index] = full;
                _canonicalBySource[full] = m.Path; // save writes the project's path back, not ours
                pairs.Add((m.Path, full));
                foreach (var dm in dto.DayMarkers)
                    if (string.Equals(dm.SourcePath, m.Path, StringComparison.OrdinalIgnoreCase)) dm.SourcePath = full;
                fixedCount++;
            }

            if (fixedCount == 0)
            {
                ClipInfo.Text = "none of the missing files are in that folder";
                continue; // let them try another folder
            }
            ClipInfo.Text = $"relinked {fixedCount} clip(s)";
            LearnMediaLocation(pairs);
            // Loop again: anything still missing gets another pass (or can be removed).
        }
    }

    /// <summary>
    /// Resolve the project's paths through this machine's media locations, in memory only. A hit
    /// records the canonical path so save can put it back.
    /// </summary>
    private void ApplyMediaLocations(ProjectDto dto)
    {
        var remaps = _settings.PathRemaps;
        if (remaps == null || remaps.Count == 0) return;
        var hits = 0;
        for (var i = 0; i < dto.Sources.Count; i++)
        {
            var p = dto.Sources[i];
            if (string.IsNullOrEmpty(p) || p.StartsWith("av://") || File.Exists(p)) continue;
            if (!PathRemapper.TryLocalize(p, remaps, out var local)) continue;
            dto.Sources[i] = local;
            _canonicalBySource[local] = p;
            hits++;
        }
        foreach (var dm in dto.DayMarkers)
            if (!string.IsNullOrEmpty(dm.SourcePath) && !File.Exists(dm.SourcePath)
                && PathRemapper.TryLocalize(dm.SourcePath, remaps, out var l)) dm.SourcePath = l;
        if (hits > 0) DiagnosticsLogger.Log($"media locations: resolved {hits} clip(s) to this machine's copy");
    }

    /// <summary>
    /// After a manual relink, derive the prefix that moved and remember it, so the next open of any
    /// project under that root needs no dialog. Only stored when every relinked pair agrees.
    /// </summary>
    private void LearnMediaLocation(IReadOnlyList<(string Old, string New)> pairs)
    {
        var learned = PathRemapper.Infer(pairs);
        if (learned == null) return;
        _settings.PathRemaps ??= new List<PathRemap>();
        if (_settings.PathRemaps.Any(r => PathRemapper.Same(r, learned))) return;
        _settings.PathRemaps.Add(learned);
        _settings.Save();
        DiagnosticsLogger.Log($"media locations: learned {learned.From} -> {learned.To}");
        ClipInfo.Text = $"relinked — remembered {learned.From} → {learned.To} for this machine";
    }

    /// <summary>The path to write into the project for a clip this machine plays from <paramref name="local"/>.</summary>
    private string CanonicalFor(string local)
    {
        if (string.IsNullOrEmpty(local)) return local;
        if (_canonicalBySource.TryGetValue(local, out var c)) return c;
        // Footage imported here that sits under a mapped root is written in the project's namespace,
        // so the shared file stays in one namespace instead of accumulating per-machine paths.
        return PathRemapper.TryCanonicalize(local, _settings.PathRemaps, out var canon) ? canon : local;
    }

    /// <summary>filename → first matching full path under <paramref name="root"/> (recursive, best effort).</summary>
    private static Dictionary<string, string> IndexByFileName(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var ext in VideoExt)
            {
                foreach (var f in Directory.EnumerateFiles(root, ext, opts))
                {
                    var n = Path.GetFileName(f);
                    if (!map.ContainsKey(n)) map[n] = f;
                }
            }
        }
        catch { /* unreadable tree → whatever we managed to index */ }
        return map;
    }

    /// <summary>Remove source indices, keeping the parallel per-clip asset list aligned.</summary>
    private static void DropSourcesAt(ProjectDto dto, HashSet<int> drop)
    {
        var hadAssets = dto.ClipAssets.Count == dto.Sources.Count;
        var keptSources = new List<string>(dto.Sources.Count);
        var keptAssets = new List<ClipAssetsDto>(dto.ClipAssets.Count);
        for (var i = 0; i < dto.Sources.Count; i++)
        {
            if (drop.Contains(i)) continue;
            keptSources.Add(dto.Sources[i]);
            if (hadAssets) keptAssets.Add(dto.ClipAssets[i]);
        }
        dto.Sources = keptSources;
        if (hadAssets) dto.ClipAssets = keptAssets;
    }

    // ---- loading splash + Home -----------------------------------------------------------------

    /// <summary>Show the loading splash - an in-window overlay above the preview row (a Popup used to be
    /// needed to beat the native mpv window's airspace; it floated over other apps when tabbing out).</summary>
    private void ShowSplash(string? project, string status)
    {
        try
        {
            SplashProject.Text = project ?? "";
            SplashStatus.Text = status;
            SplashOverlay.IsVisible = true;
        }
        catch { /* splash is cosmetic - never block loading on it */ }
    }

    private void SplashStatusText(string status)
    {
        try { if (SplashOverlay.IsVisible) SplashStatus.Text = status; } catch { }
    }

    private void HideSplash()
    {
        try { SplashOverlay.IsVisible = false; } catch { }
    }

    /// <summary>File > Home: close this project and go back to the launcher. Tears the workspace down the
    /// same way closing the app does, so nothing (mpv, ffmpeg workers, timers) is left running behind it.</summary>
    private bool _goingHome;
    private Task GoHomeAsync()
    {
        // Route through the normal close path so the existing unsaved-changes prompt (and the full
        // teardown of mpv + background workers) is reused rather than duplicated. The launcher is
        // opened from Closed, i.e. only once the close actually goes through.
        _goingHome = true;
        Close();
        return Task.CompletedTask;
    }

    private void ApplyOpenedProject(ProjectDto dto, string? path)
    {
        // Wipe the prior project clean before populating from the new dto — otherwise old clips,
        // logs, and transcripts can leak through when LoadTimeline kicks in below.
        ResetProjectState(unloadMedia: false);

        _currentProjectPath = path;
        _scrollToFurthestPending = true; // jump the manual log to the furthest entry once it's laid out
        if (!string.IsNullOrEmpty(path)) { SetProjectTitle(Path.GetFileName(path)); RememberLastProject(path); }

        // Restore offset + sync points first (so entry display times are correct even before the media
        // finishes loading). Anchors must be in place before OffsetAt() is used below.
        _timerOffset = dto.TimerOffsetSeconds;
        _offsetAnchors.Clear();
        if (dto.OffsetAnchorVideo != null && dto.OffsetAnchorTimer != null)
        {
            var n = Math.Min(dto.OffsetAnchorVideo.Length, dto.OffsetAnchorTimer.Length);
            for (var i = 0; i < n; i++)
                _offsetAnchors.Add((dto.OffsetAnchorVideo[i], dto.OffsetAnchorTimer[i]));
            _offsetAnchors.Sort((x, y) => x.VideoTime.CompareTo(y.VideoTime));
        }
        _dayMarkers = (dto.DayMarkers ?? new List<DayMarkerDto>())
            .Where(m => !string.IsNullOrEmpty(m.SourcePath))
            .Select(m => new DayMarker
            {
                SourcePath = m.SourcePath,
                Text = m.Text ?? "",
                Color = string.IsNullOrEmpty(m.Color) ? DayMarker.DefaultColor : m.Color,
            })
            .ToList();

        foreach (var ed in dto.Entries.OrderBy(x => x.T))
        {
            DateTime.TryParse(ed.CreatedAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var created);
            _entries.Add(new LogEntry
            {
                Id = string.IsNullOrEmpty(ed.Id) ? Guid.NewGuid().ToString("N") : ed.Id,
                T = ed.T, Text = ed.Text, Source = string.IsNullOrEmpty(ed.Source) ? "manual" : ed.Source,
                Color = string.IsNullOrEmpty(ed.Color) ? "#FFCB5C" : ed.Color,
                Copied = ed.Copied,
                CreatedAtUtc = created == default ? DateTime.UtcNow : created,
                OffsetSeconds = OffsetAt(ed.T),
                MergedParts = ed.MergedParts is { Count: > 0 }
                    ? ed.MergedParts.Select(p => new MergePart { T = p.T, Text = p.Text }).ToList()
                    : null,
            });
        }
        OffsetInput.Text = "";
        UpdateOffsetStatus();
        UpdateTimelineMarkers();

        // Apply slider state and stash the dto so we can paste the embedded thumbnails/waveforms/
        // transcript back into place once LoadTimeline finishes wiring up clips + audio.
        _pendingProjectAssets = dto;
        _restoredThumbs = _restoredWaves = _restoredTranscript = false;
        // Decode thumbnails/waveforms NOW on a background task so it overlaps the ffprobe + mpv load that
        // follows. Applying them later is then just array assignments on the UI thread.
        _assetDecodeTask = Task.Run(() => DecodeEmbeddedAssets(dto));

        // Per-track Pause/Level. If the project predates per-track values, fall back to the single
        // legacy SentenceTargetSec/SilenceThreshold applied to every track. InitTranscriptForTracks
        // preserves these arrays when it rebuilds, then ApplyTrackControlValues pushes them onto the
        // sliders (both run during LoadTimeline below).
        var legacyPause = dto.SentenceTargetSec is >= 0.1 and <= 60 ? dto.SentenceTargetSec : DefaultPauseSec;
        var legacyLevel = dto.SilenceThreshold is > 0 and <= 1 ? dto.SilenceThreshold : DefaultLevel;
        var nTracks = Math.Max(dto.TrackTitles.Count, Math.Max(dto.TrackPause.Count, dto.TrackLevel.Count));
        if (nTracks > 0)
        {
            _trackPause = new double[nTracks];
            _trackLevel = new double[nTracks];
            for (var t = 0; t < nTracks; t++)
            {
                _trackPause[t] = t < dto.TrackPause.Count ? dto.TrackPause[t] : legacyPause;
                _trackLevel[t] = t < dto.TrackLevel.Count ? dto.TrackLevel[t] : legacyLevel;
            }
        }
        // Per-track volume faders (applied after the audio config sets the default 1.0 array).
        _restoreVolumes = dto.TrackVolume.Count > 0 ? dto.TrackVolume.ToArray() : null;

        // View state restored after the media duration is known (see Tick).
        _restoreVideoViewport = dto.VideoPps > 0 ? (dto.VideoPps, dto.VideoScroll) : null;
        _restoreTranscriptViewport = dto.TranscriptPps > 0 ? (dto.TranscriptPps, dto.TranscriptScroll) : null;
        _restoreViewportActive = _restoreVideoViewport != null || _restoreTranscriptViewport != null;
        _restoreSeekTries = 0;
        _lastRestoreSeekTick = 0;
        _restoreSeekSettled = false;
        VideoCenterLockBtn.IsChecked = dto.VideoCenterLock;
        Timeline.LockToCenter = dto.VideoCenterLock;
        TranscriptCenterLockBtn.IsChecked = dto.TranscriptCenterLock;
        TranscriptTimeline.LockToCenter = dto.TranscriptCenterLock;
        LogCenterLockBtn.IsChecked = dto.ManualLogCenterLock;
        _logLockCenter = dto.ManualLogCenterLock;
        // Transcript column widths/collapse are applied once the tracks are (re)built (see InitTranscriptForTracks).
        _restoreTrackWeights = dto.TranscriptTrackWeights.Count > 0 ? dto.TranscriptTrackWeights.ToArray() : null;
        _restoreTrackCollapsed = dto.TranscriptTrackCollapsed.Count > 0 ? dto.TranscriptTrackCollapsed.ToArray() : null;
        ScrollLockBtn.IsChecked = dto.LockZoom;
        _lockZoom = dto.LockZoom;
        ScrollLockBtn.Content = _lockZoom ? "🔒" : "🔓";
        // Per-track auto-level (with back-compat: an old project's global flag enables all tracks).
        _autoLevelTrack = dto.AutoLevelTracks != null
            ? (bool[])dto.AutoLevelTracks.Clone()
            : Array.Empty<bool>();
        // Legacy projects stored one global flag; the real track count isn't known until the audio
        // config runs, so defer enabling-all to TryConfigureAudio via this sticky flag.
        _legacyAutoLevelAll = dto.AutoLevelTracks == null && dto.AutoLevel;
        // Auto-level Max is a global setting now (not per-project); _autoLevelTarget tracks _settings.

        if (dto.Sources.Count > 0)
        {
            // Prefer the persisted free speed; fall back to the legacy preset index for older projects.
            _restoreSpeed = dto.Speed > 0
                ? dto.Speed
                : (dto.SpeedIndex >= 0 && dto.SpeedIndex < Speeds.Length ? Speeds[dto.SpeedIndex] : 1.0);
            _restorePlayhead = dto.PlayheadSeconds;
            _restoreCenterTime = dto.PlayheadSeconds;
            // keepProjectMetadata: we just populated _entries / _pendingProjectAssets from the dto —
            // LoadTimeline must NOT wipe them.
            LoadTimeline(dto.Sources, keepProjectMetadata: true);
        }
        else
        {
            // Opening an EMPTY project (valid since M18): explicitly unload any previously-loaded media,
            // otherwise the prior project's footage/EDL/audio (and _currentSources) would leak through —
            // ResetProjectState(unloadMedia:false) above intentionally preserved them.
            try { _mpv?.RunCommandString("stop"); } catch { /* unavailable before load */ }
            _currentSources = null;
            ResetAudio();
            Timeline.SetClips(Array.Empty<TimelineClip>(), 0);
            _duration = 0;
            Timeline.SetTotal(0);
            TranscriptTimeline.SetTotal(0);
            ClipInfo.Text = "No footage loaded";
        }
    }

    // "New project" — wipes every piece of project state so you can start fresh with no inherited
    // logs/transcripts/footage. If there are unsaved changes, prompts first (Save/Discard/Cancel).
    private async Task NewProjectAsync()
    {
        if (_dirty)
        {
            var choice = await AskUnsavedChanges("Start a new project? Your unsaved work will be lost.");
            if (choice == "cancel") return;
            if (choice == "save")
            {
                await SaveProjectAsync(forceDialog: false);
                if (_dirty) return; // save failed/canceled
            }
        }
        // A new project must have a save location — pick it first; cancelling keeps the current project.
        var path = await PickProjectPathAsync();
        if (path == null) return;
        ResetProjectState(unloadMedia: true);
        _currentProjectPath = path;
        RememberLastProject(path);
        SetProjectTitle(Path.GetFileName(path));
        await SaveProjectAsync(forceDialog: false, silent: true); // write the (empty) project to disk
    }

    // Show the Save-As dialog and return the chosen .frproj filesystem path (null = cancelled / non-local).
    private async Task<string?> PickProjectPathAsync()
    {
        var top = GetTopLevel(this);
        if (top == null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project as…",
            SuggestedFileName = "review.frproj",
            DefaultExtension = "frproj",
            FileTypeChoices = new[] { new FilePickerFileType("Footage Reviewer project") { Patterns = new[] { "*.frproj" } } },
        });
        return file?.TryGetLocalPath();
    }

    // A project must exist (with a save location) before footage can be imported. Returns false if the user
    // cancels the Save-As prompt for a new project.
    private async Task<bool> EnsureProjectAsync()
    {
        if (!string.IsNullOrEmpty(_currentProjectPath)) return true;
        var path = await PickProjectPathAsync();
        if (path == null) return false;
        _currentProjectPath = path;
        RememberLastProject(path);
        SetProjectTitle(Path.GetFileName(path));
        await SaveProjectAsync(forceDialog: false, silent: true); // create the (empty) project on disk
        return true;
    }

    // Single entry point for bringing footage in (menu + drag-drop). Ensures a saved project exists first
    // (so footage can never exist without a save location). insertIndex (a clip boundary 0..count) places
    // the new clips mid-sequence; null appends. Inserting before the end remaps logs/sync points to follow
    // their clip.
    private async Task ImportFootageAsync(IReadOnlyList<string> paths, int? insertIndex = null)
    {
        if (paths == null || paths.Count == 0) return;
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit…"; return; } // serialize edits
        if (!await EnsureProjectAsync()) return; // disallow import without an active, saved project
        if (_clipReloadInFlight) return; // an edit may have started while the Save-As dialog was open

        var cur = _currentSources?.ToList() ?? new List<string>();
        if (cur.Count == 0)
        {
            // First footage into a fresh project — straight load (preserves the project's path). MarkDirty
            // only; autosave/close persists once thumbnails/waveforms have extracted (no empty-asset write).
            LoadTimeline(paths.ToList(), keepProjectMetadata: false);
            MarkDirty();
            return;
        }

        var p = Math.Clamp(insertIndex ?? cur.Count, 0, cur.Count);
        var newSources = cur.Take(p).Concat(paths).Concat(cur.Skip(p)).ToList();
        var k = paths.Count;
        // oldToNew: each existing clip's new index. Clips at/after the insert point shift right by k; the
        // newly-inserted files have no old index, so they're the only ones probed. Append (p == count) leaves
        // every existing clip in place. Seamless reload carries assets + follows logs to their clip.
        // Inserting into the middle breaks order exactly as a move does.
        if (p < cur.Count && !await ConfirmOutOfOrderAsync("Adding footage here")) return;
        if (_clipReloadInFlight) return;

        var oldToNew = new int[cur.Count];
        for (var c = 0; c < cur.Count; c++) oldToNew[c] = c < p ? c : c + k;
        BeginSeamlessClipEdit(newSources, oldToNew,
            paths.Count == 1 ? $"add {Path.GetFileName(paths[0])}" : $"add {paths.Count} clips");
        MarkDirty();
    }

    // The classic IDataObject drag-drop API (e.Data / DataFormats.Files / GetFiles) is marked obsolete in
    // Avalonia 11.3 in favour of the newer DataTransfer API, but still works and is the stable surface here.
#pragma warning disable CS0618
    // Reorder: a right-click-selected clip was dragged to a new boundary. Move it in the source list and
    // remap logs/sync points to follow their clip (same hook the mid-timeline insert uses).
    private async void OnClipMoved(int from, int toBoundary)
    {
        if (_clipReloadInFlight) return; // serialize: don't reorder while a previous edit's reload is pending
        if (_currentSources == null || _currentSources.Count == 0) return;
        var n = _currentSources.Count;
        if (from < 0 || from >= n) return;
        var insertAt = toBoundary > from ? toBoundary - 1 : toBoundary;
        insertAt = Math.Clamp(insertAt, 0, n - 1);
        if (insertAt == from) return; // dropped back where it started

        var list = _currentSources.ToList();
        var item = list[from]; list.RemoveAt(from); list.Insert(insertAt, item);

        // New order of OLD clip indices → invert to oldToNew so logs/assets can follow each clip.
        var order = Enumerable.Range(0, n).ToList();
        var oi = order[from]; order.RemoveAt(from); order.Insert(insertAt, oi);
        var oldToNew = new int[n];
        for (var newPos = 0; newPos < n; newPos++) oldToNew[order[newPos]] = newPos;

        if (!await ConfirmOutOfOrderAsync("Moving this clip")) return;
        if (_clipReloadInFlight) return; // an edit may have started while the prompt was open

        Timeline.ClearSelection();
        BeginSeamlessClipEdit(list, oldToNew, "move clip");
    }

    private void OnTimelineDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTimelineDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.Contains(DataFormats.Files)) return;
            var items = e.Data.GetFiles();
            if (items == null) return;
            var exts = new HashSet<string>(VideoExt.Select(x => x.TrimStart('*')), StringComparer.OrdinalIgnoreCase);
            var paths = items
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p) && exts.Contains(Path.GetExtension(p!)))
                .Select(p => p!)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) { ClipInfo.Text = "drop video files (mkv/mp4/mov/…) onto the timeline"; return; }
            // Insert at the boundary nearest the drop position when footage already exists; else first import.
            int? idx = (_currentSources != null && _currentSources.Count > 0)
                ? Timeline.BoundaryIndexAtX(e.GetPosition(Timeline).X)
                : null;
            await ImportFootageAsync(paths, idx);
        }
        catch (Exception ex) { ClipInfo.Text = "drop failed: " + ex.Message; }
    }
#pragma warning restore CS0618

    // The old clip index containing absolute time t (clamped to the last clip for times past the end).
    private static int ClipIndexAt(double[] starts, double[] durs, double t)
    {
        if (starts.Length == 0) return -1;
        for (var c = 0; c < starts.Length; c++)
        {
            var end = starts[c] + (c < durs.Length ? durs[c] : double.PositiveInfinity);
            if (t >= starts[c] && t < end) return c;
        }
        return starts.Length - 1;
    }

    // ---- section select → copy to Premiere -----------------------------------------------------------

    private bool _sectionActive;
    private double _sectionAnchor;     // timeline time under the cursor (the tool follows the mouse)
    private double _sectionLen = 30;   // seconds; wheel grows/shrinks it
    private bool _sectionBothWays;     // Shift → extend backwards as well as forwards
    private const double SectionMin = 1, SectionMax = 3600;

    // Default: the selection runs FORWARD from the cursor. With Shift held it straddles the cursor, covering
    // the same length either side.
    private (double Start, double End) SectionRange()
    {
        double start, end;
        if (_sectionBothWays) { start = _sectionAnchor - _sectionLen; end = _sectionAnchor + _sectionLen; }
        else { start = _sectionAnchor; end = _sectionAnchor + _sectionLen; }
        start = Math.Max(0, start);
        if (_duration > 0) end = Math.Min(_duration, end);
        return (start, Math.Max(start + 0.05, end));
    }

    private void PushSectionToTimeline()
    {
        if (!_sectionActive) { Timeline.SetSelection(null, null); return; }
        var (s, e) = SectionRange();
        Timeline.SetSelection(s, e);
    }

    // "X" — open/close the section tool. The band follows the MOUSE over the timeline; the wheel resizes it
    // (Alt+wheel still zooms), Shift makes it run both ways from the cursor.
    private void ToggleSectionSelect()
    {
        _sectionActive = !_sectionActive;
        if (_sectionActive)
        {
            if (_currentSources == null || _currentSources.Count == 0)
            {
                _sectionActive = false;
                ClipInfo.Text = "load footage first";
                return;
            }
            _sectionAnchor = CurrentTime(); // until the mouse moves over the timeline
            PushSectionToTimeline();
            ShowSectionHint();
        }
        else
        {
            Timeline.SetSelection(null, null);
            ClipInfo.Text = "section tool closed";
        }
    }

    private void ShowSectionHint()
        => ClipInfo.Text = $"section {_sectionLen:0.#}s {(_sectionBothWays ? "both ways from" : "forward from")} the cursor — " +
                           "move the mouse to aim · wheel resizes (Alt+wheel zooms) · Shift = both ways · C copies · Esc closes";

    // The cursor moved over the timeline → re-aim the selection (cheap: just re-pushes two doubles).
    private void OnSectionAnchorMoved(double t)
    {
        if (!_sectionActive) return;
        _sectionAnchor = t;
        PushSectionToTimeline();
    }

    private void OnSectionResize(int dir)
    {
        if (!_sectionActive) return;
        // Proportional steps so it scales sensibly from seconds to many minutes.
        var step = Math.Max(1, _sectionLen * 0.15);
        _sectionLen = Math.Clamp(dir > 0 ? _sectionLen + step : _sectionLen - step, SectionMin, SectionMax);
        PushSectionToTimeline();
        ShowSectionHint();
    }

    // Shift is held/released while the tool is open → flip between forward-only and both-ways.
    private void SetSectionBothWays(bool both)
    {
        if (!_sectionActive || _sectionBothWays == both) return;
        _sectionBothWays = both;
        PushSectionToTimeline();
        ShowSectionHint();
    }

    // "C" — write the selected span as an FCP7 XML sequence and put that file on the clipboard, so it can be
    // pasted (or imported) into Premiere, which then relinks the original recordings at the exact in/out.
    private async Task CopySectionForPremiereAsync()
    {
        if (!_sectionActive || _currentSources == null || _currentSources.Count == 0) return;
        var (selStart, selEnd) = SectionRange();

        // Map the span onto the source recordings it covers (a selection can straddle several clips).
        var pieces = new List<PremiereXml.Piece>();
        for (var c = 0; c < _currentSources.Count && c < _clipStarts.Length; c++)
        {
            var cs = _clipStarts[c];
            var cd = c < _clipDurations.Length ? _clipDurations[c] : 0;
            if (cd <= 0) continue;
            var ce = cs + cd;
            if (ce <= selStart || cs >= selEnd) continue;
            var src = _currentSources[c];
            if (string.IsNullOrEmpty(src) || src.StartsWith("av://") || !File.Exists(src)) continue;
            pieces.Add(new PremiereXml.Piece
            {
                Path = src,
                SourceIn = Math.Max(0, selStart - cs),
                SourceOut = Math.Min(cd, selEnd - cs),
                FileDuration = cd,
            });
        }
        if (pieces.Count == 0) { ClipInfo.Text = "no source footage in that range"; return; }

        // Match the sequence to the footage so Premiere doesn't conform it.
        var fps = 60.0;
        var w = 1920; var h = 1080;
        try
        {
            if (double.TryParse(GetStr("container-fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 1) fps = f;
            if (TryGetInt("width") is { } iw && iw > 0) w = iw;
            if (TryGetInt("height") is { } ih && ih > 0) h = ih;
        }
        catch { /* fall back to the defaults */ }

        var name = $"FR {Fmt(selStart)} – {Fmt(selEnd)}";
        var xml = PremiereXml.BuildSequence(pieces, fps, w, h, name);

        try
        {
            var dir = Path.Combine(CacheRoot, "premiere");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"section_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
            await File.WriteAllTextAsync(file, xml);

            // Handoff for the Premiere CEP panel (premiere-plugin/): it reads this and drops the footage onto
            // the open sequence at the playhead — the part a clipboard paste can't do. Written last so the
            // panel's auto-watch never sees a half-finished file.
            var handoff = new StringBuilder();
            handoff.Append("{\"fps\":").Append(fps.ToString("0.####", CultureInfo.InvariantCulture));
            handoff.Append(",\"created\":\"").Append(DateTime.Now.ToString("s")).Append("\",\"pieces\":[");
            for (var i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (i > 0) handoff.Append(',');
                handoff.Append("{\"path\":\"").Append(p.Path.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                handoff.Append(",\"inSec\":").Append(p.SourceIn.ToString("0.###", CultureInfo.InvariantCulture));
                handoff.Append(",\"outSec\":").Append(p.SourceOut.ToString("0.###", CultureInfo.InvariantCulture));
                handoff.Append('}');
            }
            handoff.Append("]}");
            await File.WriteAllTextAsync(Path.Combine(dir, "handoff.json"), handoff.ToString());

            var clip = GetTopLevel(this)?.Clipboard;
            var ok = false;
            if (clip != null)
            {
                // Put the .xml on the clipboard AS A FILE: pasting into Premiere's Project panel imports it.
                // (The classic DataObject/DataFormats.Files clipboard API is marked obsolete in Avalonia 11.3
                // in favour of DataTransfer, but it's still the working surface here — same as the drag-drop
                // handlers above.)
#pragma warning disable CS0618
                var data = new DataObject();
                var sp = GetTopLevel(this)?.StorageProvider;
                if (sp != null && await sp.TryGetFileFromPathAsync(file) is { } sf)
                {
                    data.Set(DataFormats.Files, new[] { sf });
                    await clip.SetDataObjectAsync(data);
                    ok = true;
                }
#pragma warning restore CS0618
                if (!ok) await clip.SetTextAsync(file); // fall back to the path as text
            }
            var span = selEnd - selStart;
            ClipInfo.Text = ok
                ? $"copied {span:0.#}s ({pieces.Count} clip{(pieces.Count == 1 ? "" : "s")}) — paste into Premiere's Project panel"
                : $"section written to {file}";
            DiagnosticsLogger.Log($"premiere section: {span:0.#}s, {pieces.Count} piece(s), {fps:0.##}fps → {file}");
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.LogException("CopySectionForPremiere", ex);
            ClipInfo.Text = "copy failed (logged to diagnostics.log)";
        }
    }

    // ---- seamless clip-sequence edits (reorder / insert / append / delete / chronological order) ----

    // One snapshot tuple per current clip: raw JPEG thumbnail bytes (for re-embedding) + their times, the
    // DECODED bitmaps + the waveform peaks (so they can be re-shown instantly at the clip's new index).
    private (byte[][]? Jpegs, double[]? Times, Bitmap[]? Bmps, float[][]? Waves)[] SnapshotClipAssets()
    {
        var n = _clipStarts.Length;
        var arr = new (byte[][]?, double[]?, Bitmap[]?, float[][]?)[n];
        for (var c = 0; c < n; c++)
            arr[c] = (
                _clipThumbnailJpegs != null && c < _clipThumbnailJpegs.Length ? _clipThumbnailJpegs[c] : null,
                _clipThumbnailTimes != null && c < _clipThumbnailTimes.Length ? _clipThumbnailTimes[c] : null,
                Timeline.ThumbnailsOf(c),
                Timeline.WaveformsOf(c));
        return arr;
    }

    // Carry each old clip's assets to its new index (oldToNew[c] = new index, or -1 if removed). Thumbnails
    // show immediately; waveforms are stored in _clipWaveforms and shown by PushInMemoryWaveforms once the
    // track count is re-known (the control's audio count is briefly 0 during the reconfigure).
    private void ReapplyCarriedAssets((byte[][]? Jpegs, double[]? Times, Bitmap[]? Bmps, float[][]? Waves)[] snap, int[] oldToNew)
    {
        for (var c = 0; c < snap.Length && c < oldToNew.Length; c++)
        {
            var nc = oldToNew[c];
            if (nc < 0 || nc >= _clipStarts.Length) continue;
            var (jpegs, times, bmps, waves) = snap[c];
            if (jpegs != null && _clipThumbnailJpegs != null && nc < _clipThumbnailJpegs.Length) _clipThumbnailJpegs[nc] = jpegs;
            if (times != null && _clipThumbnailTimes != null && nc < _clipThumbnailTimes.Length) _clipThumbnailTimes[nc] = times;
            if (bmps is { Length: > 0 } && times is { Length: > 0 } && bmps.Length == times.Length)
                Timeline.SetClipThumbnails(nc, bmps, times);
            if (waves != null && _clipWaveforms != null && nc < _clipWaveforms.Length)
            {
                _clipWaveforms[nc] = waves;
                for (var t = 0; t < waves.Length; t++) if (waves[t] != null) Timeline.SetClipWaveform(nc, t, waves[t]);
            }
        }
        _assetsDirty = true;
    }

    // Push any in-memory waveforms to the timeline (called after audio reconfigures, when the track count is
    // known again — until then SetClipWaveform no-ops because the control's audio count is 0).
    private void PushInMemoryWaveforms()
    {
        if (_clipWaveforms == null) return;
        var tracks = _audioIds.Length;
        for (var c = 0; c < _clipWaveforms.Length; c++)
        {
            var cell = _clipWaveforms[c];
            if (cell == null) continue;
            for (var t = 0; t < cell.Length && t < tracks; t++)
                if (cell[t] != null) Timeline.SetClipWaveform(c, t, cell[t]!);
        }
    }

    // Settings → Regenerate thumbnails / waveforms: delete the targeted on-disk cache for the loaded sources,
    // drop them from memory + the timeline, and re-extract ONLY that asset type. The other type is left
    // untouched (its in-memory data + the per-pair/cache reuse mean it isn't re-processed).
    private async Task RegenerateAssetAsync(bool thumbnails)
    {
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit…"; return; }
        if (_currentSources == null || _currentSources.Count == 0 || _clipStarts.Length == 0)
        { ClipInfo.Text = "no footage loaded"; return; }
        if (_extractTotal > 0 && _extractDone < _extractTotal)
        { ClipInfo.Text = "extraction already in progress — try again when it finishes"; return; }
        if (!thumbnails && _audioIds.Length == 0) { ClipInfo.Text = "no audio tracks to regenerate"; return; }

        var sources = _currentSources.ToList();
        var sub = thumbnails ? "thumbs" : "waves";
        await Task.Run(() => DeleteCacheFor(Path.Combine(CacheRoot, sub), sources));

        // Fresh token (the in-progress guard above means we aren't cancelling live work for the other type).
        _extractCts?.Cancel();
        _extractCts = new CancellationTokenSource();
        var ct = _extractCts.Token;

        if (thumbnails)
        {
            _clipThumbnailJpegs = new byte[_clipStarts.Length][][];
            _clipThumbnailTimes = new double[_clipStarts.Length][];
            Timeline.ClearThumbnails();
            var clips = BuildClipsArray();
            _extractKind = "thumbnails"; // progress bar reflects only what we're regenerating
            _extractTotal = clips.Length; _extractDone = 0; UpdateProgressUi();
            _ = Task.Run(() => ExtractThumbnails(clips, ct), ct);
            ClipInfo.Text = "regenerating thumbnails…";
        }
        else
        {
            _clipWaveforms = new float[_clipStarts.Length][][];
            Timeline.ClearWaveforms();
            _extractKind = "waveforms";
            _extractTotal = sources.Count * _audioIds.Length; _extractDone = 0; UpdateProgressUi();
            _ = Task.Run(() => ExtractWaveforms(sources, _audioIds.Length, ct), ct);
            ClipInfo.Text = "regenerating waveforms…";
        }
        _cachedClipAssets = null; _assetsDirty = true;
        MarkDirty();
    }

    // Delete cache files belonging to any of these sources. Match each source's EXACT cache-key prefix
    // (stem + file size + mtime ticks) rather than a bare stem substring, so a clip whose stem is a prefix of
    // another's (e.g. "clip" vs "clip_backup") can't take out the other's cache. Thumbnails are named
    // "{stem}_{len}_{ticks:X}_int…"; waveforms "v2_{stem}_{len}_{ticks:X}_a…" — both start with this key.
    private static void DeleteCacheFor(string dir, IReadOnlyList<string> sources)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var prefixes = new List<string>();
            foreach (var src in sources)
            {
                try
                {
                    var fi = new FileInfo(src);
                    var key = $"{SafeStem(src)}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks:X}_";
                    prefixes.Add(key);          // thumbnails
                    prefixes.Add("v2_" + key);  // waveforms
                }
                catch { /* unreadable source → skip */ }
            }
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    try { File.Delete(file); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    // Rebuild the TimelineClip[] from the current layout arrays (for re-running an extractor outside a reload).
    private TimelineClip[] BuildClipsArray()
    {
        var n = _clipStarts.Length;
        var clips = new TimelineClip[n];
        for (var i = 0; i < n; i++)
            clips[i] = new TimelineClip
            {
                Name = i < _clipNames.Length ? _clipNames[i] : "",
                SourcePath = _currentSources != null && i < _currentSources.Count ? _currentSources[i] : "",
                Start = _clipStarts[i],
                Duration = i < _clipDurations.Length ? _clipDurations[i] : 0,
            };
        return clips;
    }

    // Unified entry point for every clip-sequence edit. newSources = the new ordered paths; oldToNew maps each
    // CURRENT clip index → its index in newSources (or -1 if removed). Reloads the EDL WITHOUT blanking the
    // timeline, re-probing unchanged clips, or resetting play/pause, speed, zoom or playhead; carries assets +
    // audio settings to the new layout and remaps logs/sync anchors/playhead to follow their clip.
    /// <summary>
    /// True when the sequence is still exactly the natural-sort order of the clip filenames — i.e. what
    /// an import produces. Filenames rather than full paths, because that is the key the import orders
    /// by: recordings carry their date and time in the name, so it is the real chronological key.
    /// </summary>
    private bool SequenceIsInOrder()
    {
        if (_currentSources == null || _currentSources.Count < 2) return true;
        for (var i = 1; i < _currentSources.Count; i++)
            if (NaturalComparer.Instance.Compare(Path.GetFileName(_currentSources[i - 1]),
                                                 Path.GetFileName(_currentSources[i])) > 0) return false;
        return true;
    }

    /// <summary>
    /// Ask before an edit takes an ordered sequence out of order. Silent once the sequence is already
    /// out of order — the warning is about losing a property you still have, not nagging about one you
    /// gave up already.
    /// </summary>
    private async Task<bool> ConfirmOutOfOrderAsync(string action)
    {
        if (!_settings.WarnOnOutOfOrderEdit) return true;
        if (!SequenceIsInOrder()) return true;
        return await ConfirmAsync(
            "Footage is in order",
            $"The clips are currently in chronological order. {action} will put them out of order.\n\n" +
            "You can restore it afterwards with “Order chronologically”.",
            "Continue");
    }

    private void BeginSeamlessClipEdit(List<string> newSources, int[] oldToNew, string undoLabel)
    {
        if (_mpv == null) return;
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit…"; return; }
        PushClipUndo(undoLabel); // snapshot the pre-edit state for Ctrl+Z

        var oldStarts = (double[])_clipStarts.Clone();
        var oldDurs = (double[])_clipDurations.Clone();
        var oldPlayhead = _lastPlayhead;

        // Durations of carried clips are already known → only genuinely-new files (no old index) get probed.
        var knownDur = new double?[newSources.Count];
        for (var c = 0; c < oldToNew.Length; c++)
        {
            var nc = oldToNew[c];
            if (nc >= 0 && nc < knownDur.Length && c < oldDurs.Length && oldDurs[c] > 0) knownDur[nc] = oldDurs[c];
        }

        // Carry audio faders/mute/solo + visual assets across the reconfigure so nothing resets or flickers.
        _restoreVolumes = (double[])_volumes.Clone();
        _restoreMuted = (bool[])_muted.Clone();
        _restoreSolo = (bool[])_solo.Clone();
        var snap = SnapshotClipAssets();

        _afterClipsBuilt = () =>
        {
            // Asset carry is COSMETIC — isolate it so a fault in it can never skip the log remap (that was the
            // "logs don't follow the clip" bug: a throw here aborted the hook before the remap ran).
            try { ReapplyCarriedAssets(snap, oldToNew); }
            catch (Exception ex) { DiagnosticsLogger.LogException("ReapplyCarriedAssets", ex); }
            RemapTimesAfterClipEdit(oldStarts, oldDurs, oldToNew, oldPlayhead); // logs/sync anchors/playhead follow
        };
        LoadTimeline(newSources, keepProjectMetadata: true, seamlessEdit: true, knownDurations: knownDur);
    }

    // ⋮ menu → Order footage chronologically. Confirm the sort basis, compute each clip's recording time off
    // the UI thread, then reorder via the seamless edit (logs/sync points/playhead follow; Ctrl+Z undoes).
    private async Task OrderFootageChronologicallyAsync()
    {
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit…"; return; }
        if (_currentSources == null || _currentSources.Count < 2) { ClipInfo.Text = "need at least 2 clips to reorder"; return; }

        var dlg = new OrderFootageWindow();
        dlg.SetSummary($"Reorder {_currentSources.Count} clips into recording order. Logs, sync points and the playhead follow their clip, and Ctrl+Z undoes this.");
        var basis = await dlg.ShowDialog<ChronoBasis?>(this);
        if (basis == null) return;
        if (_clipReloadInFlight) return; // a reload may have started while the dialog was open
        if (_currentSources == null || _currentSources.Count < 2) return;

        var cur = _currentSources.ToList();
        var n = cur.Count;
        var keys = await Task.Run(() => cur.Select(p => ChronoKey(p, basis.Value)).ToArray());

        var idx = Enumerable.Range(0, n).ToList();
        idx.Sort((a, b) =>
        {
            var c = keys[a].CompareTo(keys[b]);
            return c != 0 ? c : string.Compare(cur[a], cur[b], StringComparison.OrdinalIgnoreCase);
        });
        if (idx.Select((v, i) => v == i).All(x => x)) { ClipInfo.Text = "footage is already in chronological order"; return; }

        var newSources = idx.Select(i => cur[i]).ToList();
        var oldToNew = new int[n];
        for (var newPos = 0; newPos < n; newPos++) oldToNew[idx[newPos]] = newPos;
        BeginSeamlessClipEdit(newSources, oldToNew, "order chronologically");
        MarkDirty();
        ClipInfo.Text = "ordered footage chronologically";
    }

    // Recording-time key for a clip. Filename basis parses the OBS-style "YYYY-MM-DD HH-MM-SS" prefix (the
    // trailing " - 04.47.18PM" is ignored), falling back to the file's modified time when the name has none.
    private static readonly System.Text.RegularExpressions.Regex ObsStamp =
        new(@"(\d{4})-(\d{2})-(\d{2})[ _T-]+(\d{2})-(\d{2})-(\d{2})", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static DateTime ChronoKey(string path, ChronoBasis basis)
    {
        try
        {
            switch (basis)
            {
                case ChronoBasis.Modified: return File.GetLastWriteTimeUtc(path);
                case ChronoBasis.Created: return File.GetCreationTimeUtc(path);
                default:
                    var m = ObsStamp.Match(Path.GetFileName(path));
                    if (m.Success
                        && int.TryParse(m.Groups[1].Value, out var y) && int.TryParse(m.Groups[2].Value, out var mo)
                        && int.TryParse(m.Groups[3].Value, out var d) && int.TryParse(m.Groups[4].Value, out var h)
                        && int.TryParse(m.Groups[5].Value, out var mi) && int.TryParse(m.Groups[6].Value, out var s))
                    {
                        try { return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Local).ToUniversalTime(); }
                        catch { /* out-of-range numbers → fall through to mtime */ }
                    }
                    try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MaxValue; }
            }
        }
        catch { return DateTime.MaxValue; }
    }

    // ---- clip-edit undo (Ctrl+Z) ----
    private sealed class ClipEditUndo
    {
        public required List<string> Sources;       // pre-edit footage order
        public required List<LogEntry> Entries;      // every entry that existed (object refs, in order)
        public required double[] EntryT;             // …and their times (restores entries a delete dropped)
        public required double[] EntryOffset;
        public required List<(double VideoTime, double TimerValue)> Anchors;
        public required double Playhead;
        public required string Label;
    }
    private readonly List<ClipEditUndo> _clipUndo = new();
    private readonly List<ClipEditUndo> _clipRedo = new();

    // Snapshot the current footage order + logs + sync anchors + playhead (the state a clip edit will change).
    private ClipEditUndo CaptureClipState(string label) => new()
    {
        Sources = _currentSources?.ToList() ?? new List<string>(),
        Entries = _entries.ToList(),
        EntryT = _entries.Select(e => e.T).ToArray(),
        EntryOffset = _entries.Select(e => e.OffsetSeconds).ToArray(),
        Anchors = _offsetAnchors.ToList(),
        Playhead = _lastPlayhead,
        Label = label,
    };

    private void PushClipUndo(string label)
    {
        if (_currentSources == null || _currentSources.Count == 0) return;
        _clipUndo.Add(CaptureClipState(label));
        if (_clipUndo.Count > 25) _clipUndo.RemoveAt(0); // bound the history
        _clipRedo.Clear(); // a fresh edit invalidates the redo stack
    }

    // Ctrl+Z — undo the most recent clip edit. Stashes the CURRENT state on the redo stack first.
    private void UndoClipEdit()
    {
        if (_mpv == null) return;
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit…"; return; }
        if (_clipUndo.Count == 0) { ClipInfo.Text = "nothing to undo"; return; }
        var u = _clipUndo[^1];
        _clipUndo.RemoveAt(_clipUndo.Count - 1);
        _clipRedo.Add(CaptureClipState(u.Label));
        if (_clipRedo.Count > 25) _clipRedo.RemoveAt(0);
        RestoreClipState(u, "undid");
    }

    // Ctrl+Shift+Z — redo the most recently undone clip edit. Stashes the CURRENT state back on the undo stack.
    private void RedoClipEdit()
    {
        if (_mpv == null) return;
        if (_clipReloadInFlight) { ClipInfo.Text = "finishing the previous edit…"; return; }
        if (_clipRedo.Count == 0) { ClipInfo.Text = "nothing to redo"; return; }
        var r = _clipRedo[^1];
        _clipRedo.RemoveAt(_clipRedo.Count - 1);
        _clipUndo.Add(CaptureClipState(r.Label));
        if (_clipUndo.Count > 25) _clipUndo.RemoveAt(0);
        RestoreClipState(r, "redid");
    }

    // Restore a captured clip state: exact footage order, logs (incl. any a delete dropped), sync anchors and
    // playhead. Reloads seamlessly (durations known by path), carrying assets so it doesn't flicker.
    private void RestoreClipState(ClipEditUndo u, string verb)
    {
        _entries.Clear();
        for (var i = 0; i < u.Entries.Count; i++)
        {
            var e = u.Entries[i];
            e.T = u.EntryT[i];
            e.OffsetSeconds = i < u.EntryOffset.Length ? u.EntryOffset[i] : OffsetAt(e.T);
            _entries.Add(e);
        }
        _offsetAnchors.Clear();
        _offsetAnchors.AddRange(u.Anchors);
        UpdateOffsetStatus();

        // Durations of the restored order are known by path from the current layout; carry assets by path too.
        var durByPath = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_currentSources != null)
            for (var i = 0; i < _currentSources.Count && i < _clipDurations.Length; i++)
                durByPath[_currentSources[i]] = _clipDurations[i];
        var knownDur = new double?[u.Sources.Count];
        for (var i = 0; i < u.Sources.Count; i++)
            knownDur[i] = durByPath.TryGetValue(u.Sources[i], out var d) && d > 0 ? d : (double?)null;

        // oldToNew (current clip index → restored index), matched greedily by path so assets carry over.
        var snap = SnapshotClipAssets();
        var oldToNew = new int[snap.Length];
        var taken = new bool[u.Sources.Count];
        for (var c = 0; c < snap.Length; c++)
        {
            oldToNew[c] = -1;
            var path = _currentSources != null && c < _currentSources.Count ? _currentSources[c] : null;
            if (path == null) continue;
            for (var j = 0; j < u.Sources.Count; j++)
                if (!taken[j] && string.Equals(u.Sources[j], path, StringComparison.OrdinalIgnoreCase)) { oldToNew[c] = j; taken[j] = true; break; }
        }

        _restoreVolumes = (double[])_volumes.Clone();
        _restoreMuted = (bool[])_muted.Clone();
        _restoreSolo = (bool[])_solo.Clone();
        var ph = u.Playhead;
        _afterClipsBuilt = () =>
        {
            ReapplyCarriedAssets(snap, oldToNew);
            if (_duration > 0) SeekTo(ph); // logs already restored to saved times → no remap, just re-seek
        };
        LoadTimeline(u.Sources, keepProjectMetadata: true, seamlessEdit: true, knownDurations: knownDur);
        MarkDirty();
        ClipInfo.Text = $"{verb}: {u.Label}";
    }

    // After a clip insert/move, remap every log + sync anchor (and the playhead) so it stays on the same
    // footage: old clip c, in-clip offset o → new clip oldToNew[c] at the new start + o. Logs whose clip was
    // removed (oldToNew[c] < 0) are dropped. _clipStarts is already the NEW layout when this runs. Transcript
    // re-derives itself from the per-clip cache during the reload's re-extraction, so it isn't remapped here.
    private void RemapTimesAfterClipEdit(double[] oldStarts, double[] oldDurs, int[] oldToNew, double oldPlayhead)
    {
        // Clamp against the NEW clip layout's total, known synchronously here (_clipStarts/_clipDurations
        // were just set). Do NOT use _duration — it's polled from mpv on the 120 ms timer and still holds
        // the OLD (smaller) total when this hook runs, which would collapse logs on shifted-later clips.
        var newTotal = _clipStarts.Length > 0
            ? _clipStarts[^1] + (_clipDurations.Length > 0 ? _clipDurations[^1] : 0)
            : 0;
        double? NewT(double t)
        {
            var c = ClipIndexAt(oldStarts, oldDurs, t);
            if (c < 0) return t;
            var nc = c < oldToNew.Length ? oldToNew[c] : -1;
            if (nc < 0) return null;                       // clip removed → drop
            if (nc >= _clipStarts.Length) return t;
            var nt = _clipStarts[nc] + (t - oldStarts[c]);
            return newTotal > 0 ? Math.Clamp(nt, 0, newTotal) : Math.Max(0, nt);
        }

        var before = _entries.Count;
        var survivors = new List<LogEntry>(_entries.Count);
        foreach (var e in _entries)
            if (NewT(e.T) is { } nt) { e.T = nt; survivors.Add(e); }
        survivors.Sort((a, b) => a.T.CompareTo(b.T));
        _entries.Clear();
        foreach (var e in survivors) _entries.Add(e);
        DiagnosticsLogger.Log($"clip edit: remapped {survivors.Count}/{before} logs to follow their clip");

        for (var i = _offsetAnchors.Count - 1; i >= 0; i--)
        {
            if (NewT(_offsetAnchors[i].VideoTime) is { } nv) _offsetAnchors[i] = (nv, _offsetAnchors[i].TimerValue);
            else _offsetAnchors.RemoveAt(i);
        }
        _offsetAnchors.Sort((a, b) => a.VideoTime.CompareTo(b.VideoTime));
        foreach (var e in _entries) e.OffsetSeconds = OffsetAt(e.T); // refresh display offsets from remapped anchors
        UpdateOffsetStatus();

        if (_duration > 0 && NewT(oldPlayhead) is { } np) SeekTo(np); // keep the user on the same content
        // Mark dirty only — do NOT save here: the transcript/assets were just cleared and re-extraction
        // hasn't repopulated them yet, so an immediate save would persist an empty transcript/assets.
        // Autosave + close-save (which run after re-extraction) persist the full, remapped project.
        MarkDirty();
    }

    // Centralized reset. unloadMedia=true also asks mpv to stop and clears the current EDL.
    private void ResetProjectState(bool unloadMedia)
    {
        // Cancel any in-flight extraction so it doesn't write to our cleared state.
        _extractCts?.Cancel();
        _transcribeCts?.Cancel();
        _transcribePause.Set();
        _transcribePaused = false;
        TranscribeControls.IsVisible = false;
        TranscriptProgress.IsVisible = false;
        _extractTotal = 0;
        _extractDone = 0;
        UpdateProgressUi();

        // Manual log + offset
        _entries.Clear();
        _groups.Clear();
        _dayMarkers.Clear(); // else the previous project's day dividers leak into the new one
        _canonicalBySource.Clear();
        _timerOffset = 0;
        _offsetAnchors.Clear();
        _pendingEntryTime = null;
        OffsetInput.Text = "";
        EntryInput.Text = "";
        PendingHint.Text = "";
        UpdateOffsetStatus();

        // Transcript
        foreach (var col in _trackSegments) col.Clear();
        foreach (var raw in _rawSegmentsByTrack) raw.Clear();
        for (var i = 0; i < _activeSegmentPerTrack.Length; i++) _activeSegmentPerTrack[i] = null;
        TranscriptStatus.Text = "Transcribes every audio track on the GPU. Click Generate to start.";
        if (unloadMedia)
        {
            _trackPause = Array.Empty<double>();
            _trackLevel = Array.Empty<double>();
            TrackControlsStrip.Children.Clear();
            TrackControlsStrip.ColumnDefinitions.Clear();
            _trackPauseSliders.Clear();
            _trackPauseVals.Clear();
        }

        // View toggles
        VideoCenterLockBtn.IsChecked = false; Timeline.LockToCenter = false;
        TranscriptCenterLockBtn.IsChecked = false; TranscriptTimeline.LockToCenter = false;
        LogCenterLockBtn.IsChecked = false; _logLockCenter = false;
        ScrollLockBtn.IsChecked = false; _lockZoom = false; ScrollLockBtn.Content = "🔓";
        _restoreVideoViewport = null; _restoreTranscriptViewport = null; _restoreViewportActive = false;
        _restoreCenterTime = 0; _restoreSeekTries = 0; _lastRestoreSeekTick = 0; _restoreSeekSettled = false;
        _restoreVolumes = null;
        _autoLevelTrack = Array.Empty<bool>();
        _legacyAutoLevelAll = false;
        _autoChunks = Array.Empty<List<(double, double, double)>>();
        _tempMarker = null; Timeline.SetTempMarker(null);
        _changeSeq = 0; _lastBackupSeq = 0; _savedContentSeq = 0;   // fresh change-tracking for the new/opened project
        _scrollToFurthestPending = false;

        // Timeline + clip layout + asset mirrors
        _clipStarts = Array.Empty<double>();
        _clipDurations = Array.Empty<double>();
        _clipNames = Array.Empty<string>();
        _clipThumbnailJpegs = null;
        _clipThumbnailTimes = null;
        _clipWaveforms = null;
        _cachedClipAssets = null; _assetsDirty = true; // drop the encoded-asset cache for the new project
        Timeline.SetClips(Array.Empty<TimelineClip>(), 0);
        Timeline.SetMarkers(Array.Empty<double>(), Array.Empty<string>());
        // SetClips no longer zeros the total (so a 0 ffprobe sum can't wipe it); clear it explicitly
        // here so New Project / unload visually empties the timelines.
        _duration = 0;
        Timeline.SetTotal(0);
        TranscriptTimeline.SetTotal(0);

        // Audio / EDL
        if (unloadMedia)
        {
            try { _mpv?.RunCommandString("stop"); } catch { }
            _currentSources = null;
            ResetAudio();
            ClipInfo.Text = "No footage loaded";
        }

        _afterClipsBuilt = null; // backstop: a New/Open must never inherit a stale clip-edit remap hook

        // Project file + dirty. Only a full unload (New Project) drops the save location — a footage
        // import keeps the project's path so a project always has a save location once created.
        _pendingProjectAssets = null;
        _assetDecodeTask = null;
        if (unloadMedia)
        {
            _currentProjectPath = null;
            _lastSavedUtc = null;
            Title = "Footage Reviewer";
        }
        ClearDirty();
        UpdateSaveStatus();
    }

    private async Task<string> AskUnsavedChanges(string message)
    {
        var box = new Window
        {
            Title = "Unsaved changes", Width = 420, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false, Background = Brush.Parse("#0F0F14"),
        };
        var save = new Button { Content = "Save", MinWidth = 80, Background = Brush.Parse("#274050"), Foreground = Brushes.White, Focusable = false };
        var discard = new Button { Content = "Discard", MinWidth = 80, Focusable = false };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, Focusable = false };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { save, discard, cancel },
        };
        var body = new TextBlock
        {
            Text = message,
            Foreground = Brush.Parse("#E2E2EA"), Margin = new Avalonia.Thickness(16, 18, 16, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var layout = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(body, Avalonia.Controls.Dock.Top);
        layout.Children.Add(body);
        layout.Children.Add(new Border { Child = buttons, Margin = new Avalonia.Thickness(16, 12, 16, 0) });
        box.Content = layout;
        string choice = "cancel";
        save.Click += (_, _) => { choice = "save"; box.Close(); };
        discard.Click += (_, _) => { choice = "discard"; box.Close(); };
        cancel.Click += (_, _) => { choice = "cancel"; box.Close(); };
        await box.ShowDialog(this);
        return choice;
    }

    // Called from BuildTimelineClipsAsync (after clip layout exists) and TryConfigureAudio (after
    // audio tracks exist). Each side restores the assets it has the structure for, then the dto
    // is cleared when both have run.
    // ---- embedded assets: decoded off-thread, applied on the UI thread -------------------------------

    private sealed class DecodedAssets
    {
        public ProjectDto Dto = null!;
        public byte[][]?[] ThumbBytes = Array.Empty<byte[][]?>();
        public double[]?[] ThumbTimes = Array.Empty<double[]?>();
        public Bitmap[]?[] Bitmaps = Array.Empty<Bitmap[]?>();
        public float[]?[]?[] Waves = Array.Empty<float[]?[]?>(); // [clip][track]
        public long DecodeMs;
    }

    private Task<DecodedAssets>? _assetDecodeTask;
    private bool _restoredThumbs, _restoredWaves, _restoredTranscript;

    /// <summary>Run <paramref name="action"/> on the UI thread once the embedded-asset decode has finished
    /// (immediately if there is none or it's already done).</summary>
    private void RunWhenAssetsDecoded(Action action)
    {
        var t = _assetDecodeTask;
        if (t == null || t.IsCompleted) { action(); return; }
        t.ContinueWith(_ => Dispatcher.UIThread.Post(action), TaskScheduler.Default);
    }

    /// <summary>The expensive half: base64 -> JPEG bytes -> Bitmap for every embedded thumbnail, and base64 ->
    /// float[] for every waveform. Pure CPU, parallel over clips, no UI state touched. Bitmaps are safe to
    /// construct off the UI thread (the extractor already does).</summary>
    private static DecodedAssets DecodeEmbeddedAssets(ProjectDto dto)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = dto.ClipAssets.Count;
        var r = new DecodedAssets
        {
            Dto = dto,
            ThumbBytes = new byte[][]?[n], ThumbTimes = new double[]?[n], Bitmaps = new Bitmap[]?[n], Waves = new float[]?[]?[n],
        };
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6) };
        Parallel.For(0, n, opts, c =>
        {
            var a = dto.ClipAssets[c];
            if (a.ThumbnailsB64.Count > 0)
            {
                var bytes = new List<byte[]>(a.ThumbnailsB64.Count);
                var bmps = new List<Bitmap>(a.ThumbnailsB64.Count);
                var times = new List<double>(a.ThumbnailsB64.Count);
                for (var i = 0; i < a.ThumbnailsB64.Count; i++)
                {
                    try
                    {
                        var b = Convert.FromBase64String(a.ThumbnailsB64[i]);
                        using var ms = new MemoryStream(b);
                        var bmp = new Bitmap(ms);
                        bytes.Add(b); bmps.Add(bmp); times.Add(i < a.ThumbnailTimes.Count ? a.ThumbnailTimes[i] : 0);
                    }
                    catch { /* a corrupt frame just drops out of the filmstrip */ }
                }
                if (bmps.Count > 0) { r.ThumbBytes[c] = bytes.ToArray(); r.Bitmaps[c] = bmps.ToArray(); r.ThumbTimes[c] = times.ToArray(); }
            }
            var tc = Math.Max(a.WaveU8.Count, a.WaveformsB64.Count);
            if (tc > 0)
            {
                var w = new float[]?[tc];
                for (var t = 0; t < tc; t++)
                {
                    try
                    {
                        if (t < a.WaveU8.Count && !string.IsNullOrEmpty(a.WaveU8[t]))
                        {
                            var raw = Convert.FromBase64String(a.WaveU8[t]!);
                            var peaks = new float[raw.Length];
                            for (var p = 0; p < raw.Length; p++) peaks[p] = raw[p] / 255f;
                            w[t] = peaks;
                        }
                        else if (t < a.WaveformsB64.Count && !string.IsNullOrEmpty(a.WaveformsB64[t])) // legacy float32
                        {
                            var raw = Convert.FromBase64String(a.WaveformsB64[t]!);
                            var peaks = new float[raw.Length / 4];
                            Buffer.BlockCopy(raw, 0, peaks, 0, peaks.Length * 4);
                            w[t] = peaks;
                        }
                    }
                    catch { w[t] = null; }
                }
                r.Waves[c] = w;
            }
        });
        r.DecodeMs = sw.ElapsedMilliseconds;
        return r;
    }

    /// <summary>The cheap half: hand the decoded assets to the timeline. Self-defers until the decode task is
    /// done, and each section (thumbnails / waveforms / transcript) runs exactly once per opened project.</summary>
    private void TryRestoreEmbeddedAssets()
    {
        var dto = _pendingProjectAssets;
        if (dto == null) return;
        var task = _assetDecodeTask;
        if (task == null) { _pendingProjectAssets = null; return; }
        if (!task.IsCompleted)
        {
            task.ContinueWith(_ => Dispatcher.UIThread.Post(TryRestoreEmbeddedAssets), TaskScheduler.Default);
            return;
        }
        if (task.IsFaulted)
        {
            DiagnosticsLogger.LogException("embedded asset decode", task.Exception!);
            _pendingProjectAssets = null; _assetDecodeTask = null;
            return;
        }
        var d = task.Result;
        if (!ReferenceEquals(d.Dto, dto))
        {
            // Decode belongs to a project that has since been replaced - free its bitmaps and bail.
            foreach (var arr in d.Bitmaps) if (arr != null) foreach (var b in arr) { try { b.Dispose(); } catch { } }
            _assetDecodeTask = null;
            return;
        }
        NoteHeavyOp("RestoreEmbeddedAssets(apply)");
        var swR = System.Diagnostics.Stopwatch.StartNew(); long msThumbs = 0, msWaves = 0, msTx = 0;

        // Thumbnails + waveforms need _clipStarts (clip layout); waveforms also need the audio track count.
        var clipsReady = _clipStarts.Length == dto.Sources.Count;
        var audioReady = _audioConfigured;

        if (clipsReady && !_restoredThumbs && _clipThumbnailJpegs != null && _clipThumbnailTimes != null)
        {
            _restoredThumbs = true;
            for (var c = 0; c < d.Bitmaps.Length && c < _clipThumbnailJpegs.Length; c++)
            {
                var bmps = d.Bitmaps[c];
                if (bmps == null || bmps.Length == 0) continue;
                _clipThumbnailJpegs[c] = d.ThumbBytes[c]!;
                _clipThumbnailTimes[c] = d.ThumbTimes[c]!;
                Timeline.SetClipThumbnails(c, bmps, d.ThumbTimes[c]!);
            }
            msThumbs = swR.ElapsedMilliseconds;
        }

        if (clipsReady && audioReady && !_restoredWaves && _clipWaveforms != null)
        {
            _restoredWaves = true;
            var trackCount = _audioIds.Length;
            for (var c = 0; c < d.Waves.Length && c < _clipWaveforms.Length; c++)
            {
                var cell = _clipWaveforms[c] = new float[trackCount][];
                var w = d.Waves[c];
                if (w == null) continue;
                for (var t = 0; t < trackCount && t < w.Length; t++)
                {
                    var peaks = w[t];
                    if (peaks == null) continue;
                    cell[t] = peaks;
                    Timeline.SetClipWaveform(c, t, peaks);
                }
            }
            msWaves = swR.ElapsedMilliseconds - msThumbs;
        }

        if (audioReady && !_restoredTranscript && _trackSegments.Count > 0 && dto.Transcript.Count > 0)
        {
            _restoredTranscript = true;
            NoteHeavyOp("RestoreEmbeddedAssets(transcript)");
            for (var t = 0; t < _rawSegmentsByTrack.Count && t < dto.Transcript.Count; t++)
            {
                var src = dto.Transcript[t];
                _rawSegmentsByTrack[t].Clear();
                foreach (var sg in src.OrderBy(x => x.Start))
                {
                    _rawSegmentsByTrack[t].Add(new TranscriptSegment
                    {
                        Start = sg.Start, End = sg.End, Text = sg.Text, Speaker = sg.Speaker,
                    });
                }
                RebuildTrackDisplay(t);
            }
            if (_rawSegmentsByTrack.Any(c => c.Count > 0))
            {
                var total = _rawSegmentsByTrack.Sum(c => c.Count);
                TranscriptStatus.Text = $"Transcript ready · {total} lines.";
            }
            msTx = swR.ElapsedMilliseconds - msThumbs - msWaves;
            DiagnosticsLogger.Log($"transcript restored from project: {string.Join("/", _rawSegmentsByTrack.Select(c => c.Count))} segments in {msTx} ms");
        }

        _assetsDirty = true; // restored assets -> rebuild the encoded-asset cache on next save
        if (clipsReady && audioReady)
        {
            _pendingProjectAssets = null;
            _assetDecodeTask = null;
            DiagnosticsLogger.Log($"embedded assets restored: {d.Bitmaps.Length} clips, decoded in {d.DecodeMs} ms off the UI thread; apply: thumbs {msThumbs} ms, waves {msWaves} ms, transcript {msTx} ms");
            if (AnyAutoLevel) RecomputeAutoLevel(); // waveforms are in place now
        }
    }

    // ---- Auto transcript --------------------------------------------------

    private static readonly JsonSerializerOptions EngineJsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private sealed class EngineConfig
    {
        public string Python { get; set; } = "";
        public string Script { get; set; } = "";
        public string Model { get; set; } = "medium";
        public string Device { get; set; } = "cuda";
        public string Compute { get; set; } = "float16";
    }

    private sealed class EngineOut { public List<EngineSeg> Segments { get; set; } = new(); }
    private sealed class EngineSeg { public double Start { get; set; } public double End { get; set; } public string Text { get; set; } = ""; }

    // Locate a file under native\whisper relative to the build output, or the dev tree.
    private static string? WhisperFile(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            // Shipped inside the app (macOS .app bundles copy native/whisper next to the binary).
            Path.Combine(baseDir, "whisper", name),
            Path.Combine(baseDir, "native", "whisper", name),
        };

        // Dev-tree run: walk up from the build output until we find native/whisper.
        var dir = baseDir;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            candidates.Add(Path.GetFullPath(Path.Combine(dir, "native", "whisper", name)));
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The whisper engine config for this OS. engine.json holds the Windows/CUDA setup; other platforms
    /// get their own file (engine.macos.json) so the two never overwrite each other in the repo.
    /// </summary>
    private static string? WhisperConfigFile()
    {
        if (OperatingSystem.IsMacOS()) return WhisperFile("engine.macos.json") ?? WhisperFile("engine.json");
        if (OperatingSystem.IsLinux()) return WhisperFile("engine.linux.json") ?? WhisperFile("engine.json");
        return WhisperFile("engine.json");
    }

    private void HighlightTranscriptAt(double pos)
    {
        if (_trackSegments.Count == 0) return;
        var changed = false;
        for (var t = 0; t < _trackSegments.Count; t++)
        {
            var col = _trackSegments[t];
            TranscriptSegment? active = null;
            foreach (var s in col)
            {
                if (pos >= s.Start && pos < s.End) { active = s; break; }
                if (s.Start > pos) break;
            }
            if (t < _activeSegmentPerTrack.Length && ReferenceEquals(active, _activeSegmentPerTrack[t])) continue;
            if (t < _activeSegmentPerTrack.Length && _activeSegmentPerTrack[t] != null)
                _activeSegmentPerTrack[t]!.IsActive = false;
            if (t < _activeSegmentPerTrack.Length) _activeSegmentPerTrack[t] = active;
            if (active != null) active.IsActive = true;
            changed = true;
        }
        if (changed) TranscriptTimeline.InvalidateVisual();
        // The transcript playhead is driven by SmoothTick (~60 Hz) from the interpolated time; pushing
        // the raw 8 Hz pos here would reintroduce the chunky motion.
    }

    private void SyncTranscriptViewportFromVideo()
        => TranscriptTimeline.SetViewport(Timeline.PixelsPerSecond, Timeline.ScrollSeconds);

    private void AutoSizeVideoColumn()
    {
        if (_videoWidthLocked) return;
        if (PreviewRow == null) return;
        var h = PreviewRow.Bounds.Height;
        if (h <= 0) return;
        var totalW = PreviewRow.Bounds.Width;
        var target = Math.Max(320, h * 16.0 / 9.0);
        // Keep the side panel (manual log + transcript) at no less than ~1/3 of the width — otherwise a
        // tall/maximised window lets the 16:9 video swallow it. Below that, video stays true 16:9.
        if (totalW > 0)
        {
            var splitterW = PreviewRow.ColumnDefinitions.Count > 1 ? PreviewRow.ColumnDefinitions[1].Width.Value : 10;
            var maxVideo = Math.Max(320, totalW * 2.0 / 3.0 - splitterW);
            target = Math.Min(target, maxVideo);
        }
        var col = PreviewRow.ColumnDefinitions[0];
        if (Math.Abs(col.Width.Value - target) > 1)
        {
            col.Width = new GridLength(target);
            _lastAutoVideoWidth = target;
        }
    }

    private void InitTranscriptForTracks(IReadOnlyList<string> titles)
    {
        _trackSegments.Clear();
        _rawSegmentsByTrack.Clear();
        _activeSegmentPerTrack = new TranscriptSegment?[titles.Count];
        // Preserve any per-track slider values already set (e.g. restored from a project before the
        // audio config finished); otherwise default.
        var oldPause = _trackPause; var oldLevel = _trackLevel;
        _trackPause = new double[titles.Count];
        _trackLevel = new double[titles.Count];
        for (var t = 0; t < titles.Count; t++)
        {
            _trackPause[t] = t < oldPause.Length ? oldPause[t] : DefaultPauseSec;
            _trackLevel[t] = t < oldLevel.Length ? oldLevel[t] : DefaultLevel;
            _trackSegments.Add(new List<TranscriptSegment>());
            _rawSegmentsByTrack.Add(new List<TranscriptSegment>());
        }
        TranscriptTimeline.SetTracks(titles);
        // Re-apply saved column widths / collapsed tracks now that SetTracks reset them to defaults.
        if (_restoreTrackWeights != null || _restoreTrackCollapsed != null)
        {
            TranscriptTimeline.RestoreLayout(_restoreTrackWeights, _restoreTrackCollapsed);
            _restoreTrackWeights = null;
            _restoreTrackCollapsed = null;
        }
        TranscriptTimeline.SetTotal(_duration);
        BuildTrackControlStrip(titles);
        BuildLevelSettings(titles);
    }

    // One compact cell per track: the track name + two toggles — 👁 hide this track's bubbles, and
    // ⊘ hide the whole column (the others widen to fill). The gap-length sliders now live in the ⋮ menu.
    private void BuildTrackControlStrip(IReadOnlyList<string> titles)
    {
        TrackControlsStrip.Children.Clear();
        TrackControlsStrip.ColumnDefinitions.Clear();

        for (var t = 0; t < titles.Count; t++)
        {
            TrackControlsStrip.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var idx = t;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(new TextBlock
            {
                Text = $"Track {t + 1}", FontSize = 11, Foreground = Brush.Parse("#9A9AA8"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 2, 0),
            });

            var hideBubblesBtn = new ToggleButton
            {
                Content = "👁", Width = 28, Height = 22, FontSize = 11, Padding = new Avalonia.Thickness(0),
                Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                [ToolTip.TipProperty] = "Hide / show this track's transcript bubbles (column stays)",
            };
            hideBubblesBtn.Click += (_, _) => TranscriptTimeline.SetTrackHidden(idx, hideBubblesBtn.IsChecked == true);

            var hideTrackBtn = new ToggleButton
            {
                Content = "⊘", Width = 28, Height = 22, FontSize = 12, Padding = new Avalonia.Thickness(0),
                Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsChecked = TranscriptTimeline.IsTrackCollapsed(idx),
                [ToolTip.TipProperty] = "Hide this track entirely — the remaining tracks expand to fill",
            };
            hideTrackBtn.Click += (_, _) =>
            {
                TranscriptTimeline.SetTrackCollapsed(idx, hideTrackBtn.IsChecked == true);
                hideTrackBtn.IsChecked = TranscriptTimeline.IsTrackCollapsed(idx); // may refuse the last one
                MarkDirty(contentChanged: false);
            };

            row.Children.Add(hideBubblesBtn);
            row.Children.Add(hideTrackBtn);

            var cell = new Border
            {
                Background = Brush.Parse("#101018"), CornerRadius = new CornerRadius(4),
                Margin = new Avalonia.Thickness(2), Padding = new Avalonia.Thickness(2),
                Child = row,
            };
            Grid.SetColumn(cell, t);
            TrackControlsStrip.Children.Add(cell);
        }
    }

    // The transcript ⋮ flyout: mirrors the global Settings "Auto Transcript" section (synced via
    // _settings) PLUS the per-track silence levels (project state). Rebuilt every time the flyout opens
    // so it reflects changes made in the global Settings window.
    private void BuildLevelSettings(IReadOnlyList<string> titles)
    {
        LevelsPanel.Children.Clear();

        TextBlock Head(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#BFD2F2"), FontSize = 12, Margin = new Avalonia.Thickness(0, 2, 0, 2) };
        Grid LabeledRow(string label, Control control)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            var l = new TextBlock { Text = label, Foreground = Brush.Parse("#D8D8E2"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(l, 0); Grid.SetColumn(control, 1);
            g.Children.Add(l); g.Children.Add(control);
            return g;
        }

        // ---- mirrored global "Auto Transcript" settings ----
        LevelsPanel.Children.Add(Head("Auto Transcript settings"));

        var onLoad = new CheckBox { Content = "Auto-generate on load", Foreground = Brush.Parse("#E2E2EA"), IsChecked = _settings.TranscribeOnLoad, FontSize = 12 };
        onLoad.IsCheckedChanged += (_, _) => { _settings.TranscribeOnLoad = onLoad.IsChecked == true; _settings.Save(); };
        LevelsPanel.Children.Add(onLoad);

        var font = new NumericUpDown { Minimum = 8, Maximum = 28, Increment = 1, Value = (decimal)_settings.TranscriptFontSize, Width = 132 };
        font.ValueChanged += (_, _) => { if (font.Value is decimal v) { _settings.TranscriptFontSize = (double)v; TranscriptTimeline.SetFontSize((double)v); _settings.Save(); } };
        LevelsPanel.Children.Add(LabeledRow("Bubble font size", font));

        var amax = new NumericUpDown { Minimum = 0.4m, Maximum = 1.0m, Increment = 0.05m, FormatString = "0.00", Value = (decimal)_settings.AutoLevelMax, Width = 132 };
        amax.ValueChanged += (_, _) => { if (amax.Value is decimal v) { _settings.AutoLevelMax = (double)v; _autoLevelTarget = (double)v; _settings.Save(); if (AnyAutoLevel) RecomputeAutoLevel(); } };
        LevelsPanel.Children.Add(LabeledRow("Auto-level max", amax));

        var p2chk = new CheckBox { Content = "Track 2 prefix (Me:)", Foreground = Brush.Parse("#E2E2EA"), IsChecked = _settings.Track2PrefixEnabled, FontSize = 12 };
        var p2 = new TextBox { Text = _settings.Track2Prefix, Width = 120, FontSize = 12 };
        p2chk.IsCheckedChanged += (_, _) => { _settings.Track2PrefixEnabled = p2chk.IsChecked == true; _settings.Save(); };
        p2.LostFocus += (_, _) => { _settings.Track2Prefix = (p2.Text ?? "").Trim(); _settings.Save(); };
        LevelsPanel.Children.Add(p2chk);
        LevelsPanel.Children.Add(LabeledRow("  prefix text", p2));

        var p3chk = new CheckBox { Content = "Track 3 prefix (Chatter:)", Foreground = Brush.Parse("#E2E2EA"), IsChecked = _settings.Track3PrefixEnabled, FontSize = 12 };
        var p3 = new TextBox { Text = _settings.Track3Prefix, Width = 120, FontSize = 12 };
        p3chk.IsCheckedChanged += (_, _) => { _settings.Track3PrefixEnabled = p3chk.IsChecked == true; _settings.Save(); };
        p3.LostFocus += (_, _) => { _settings.Track3Prefix = (p3.Text ?? "").Trim(); _settings.Save(); };
        LevelsPanel.Children.Add(p3chk);
        LevelsPanel.Children.Add(LabeledRow("  prefix text", p3));

        LevelsPanel.Children.Add(new Border { Height = 1, Background = Brush.Parse("#26262F"), Margin = new Avalonia.Thickness(0, 4, 0, 4) });

        // ---- per-track silence level (project state) ----
        LevelsPanel.Children.Add(Head("Silence level per track"));
        LevelsPanel.Children.Add(new TextBlock
        {
            Text = "Waveform level below which a track counts as silent (0.01–0.50). Lower = picks up quieter speech.",
            Foreground = Brush.Parse("#7E7E8C"), FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
        });

        for (var t = 0; t < titles.Count; t++)
        {
            var idx = t;
            var lastLevel = _trackLevel[idx];
            var box = new TextBox { Text = $"{_trackLevel[idx]:0.00}", Width = 66, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            void CommitLevel()
            {
                if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    v = Math.Clamp(v, 0.01, 0.5);
                    _trackLevel[idx] = v;
                    box.Text = $"{v:0.00}";
                    if (Math.Abs(v - lastLevel) > 0.0005) { lastLevel = v; ScheduleTranscriptRebuild(); MarkDirty(); } // only on change
                }
                else box.Text = $"{_trackLevel[idx]:0.00}";
            }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitLevel(); e.Handled = true; } };
            box.LostFocus += (_, _) => CommitLevel();
            LevelsPanel.Children.Add(LabeledRow($"Track {t + 1}", box));
        }

        LevelsPanel.Children.Add(new Border { Height = 1, Background = Brush.Parse("#26262F"), Margin = new Avalonia.Thickness(0, 4, 0, 4) });

        // ---- per-track Gap length (moved here from the track strip) ----
        LevelsPanel.Children.Add(Head("Gap length per track"));
        LevelsPanel.Children.Add(new TextBlock
        {
            Text = "Silence (seconds) that ends a bubble on a track (0.2–10). Drag to set; release to apply.",
            Foreground = Brush.Parse("#7E7E8C"), FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
        });
        _trackPauseSliders.Clear();
        _trackPauseVals.Clear();
        for (var t = 0; t < titles.Count && t < _trackPause.Length; t++)
        {
            var idx = t;
            var init = Math.Clamp(_trackPause[idx], GapMinSec, GapMaxSec);
            _trackPause[idx] = init;
            var lastApplied = init;

            var gslider = new Slider
            {
                Orientation = Orientation.Horizontal, Minimum = GapMinSec, Maximum = GapMaxSec, Value = init,
                Width = 150, VerticalAlignment = VerticalAlignment.Center,
                [ToolTip.TipProperty] = "Drag to set; release to apply.",
            };
            var gval = new TextBox
            {
                Text = $"{init:0.0}", Width = 56, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                TextAlignment = TextAlignment.Center,
            };
            gslider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                _trackPause[idx] = gslider.Value;
                if (!gval.IsFocused) gval.Text = $"{gslider.Value:0.0}";
            };
            void ApplyGapIfChanged()
            {
                if (Math.Abs(_trackPause[idx] - lastApplied) < 0.001) return;
                lastApplied = _trackPause[idx];
                ScheduleTranscriptRebuild();
                MarkDirty();
            }
            gslider.AddHandler(PointerReleasedEvent, (_, _) => ApplyGapIfChanged(),
                               RoutingStrategies.Tunnel, handledEventsToo: true);
            void CommitGap()
            {
                if (double.TryParse(gval.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    v = Math.Clamp(v, GapMinSec, GapMaxSec);
                    _trackPause[idx] = v;
                    gslider.Value = v;
                    gval.Text = $"{v:0.0}";
                    ApplyGapIfChanged();
                }
                else gval.Text = $"{_trackPause[idx]:0.0}";
            }
            gval.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitGap(); e.Handled = true; } };
            gval.LostFocus += (_, _) => CommitGap();

            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            rowPanel.Children.Add(new TextBlock { Text = $"Track {t + 1}", Width = 56, Foreground = Brush.Parse("#D8D8E2"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            rowPanel.Children.Add(gslider);
            rowPanel.Children.Add(gval);
            LevelsPanel.Children.Add(rowPanel);

            _trackPauseSliders.Add(gslider);
            _trackPauseVals.Add(gval);
        }
    }


    private void ToggleTranscribePause()
    {
        _transcribePaused = !_transcribePaused;
        if (_transcribePaused) { _transcribePause.Reset(); PauseTranscriptBtn.Content = "▶"; PauseTranscriptBtn.SetValue(ToolTip.TipProperty, "Resume transcription"); }
        else { _transcribePause.Set(); PauseTranscriptBtn.Content = "⏸"; PauseTranscriptBtn.SetValue(ToolTip.TipProperty, "Pause transcription"); }
    }

    // Stop button — fully cancel transcription and return to the idle state (Generate re-enabled). Already-done
    // clips stay (they're cached + applied); only the remaining queue is abandoned.
    private void StopTranscription()
    {
        _transcribeCts?.Cancel();
        _transcribePause.Set();   // unblock the worker if it's paused so it can observe the cancel
        _transcribePaused = false;
        TranscribeControls.IsVisible = false;
        PauseTranscriptBtn.Content = "⏸";
        GenerateBtn.IsEnabled = true;
        GenerateBtn.Content = "Generate";
        TranscriptProgress.IsVisible = false;
        TranscriptStatus.Text = "Transcription stopped.";
    }

    // Kept for completeness if/when we add a Log-this-segment button back to the transcript timeline.
    private void LogFromSegment(TranscriptSegment s)
    {
        var text = string.IsNullOrEmpty(s.Speaker) ? s.Text : $"[{s.Speaker}] {s.Text}";
        InsertEntrySorted(new LogEntry { T = s.Start, Text = text, Source = "transcript", OffsetSeconds = OffsetAt(s.Start) });
    }

    private void InsertSegmentSorted(int trackIdx, TranscriptSegment seg)
    {
        if (trackIdx < 0 || trackIdx >= _rawSegmentsByTrack.Count) return;
        var raw = _rawSegmentsByTrack[trackIdx];
        // raw is kept time-sorted; clips may finish out of playhead order so insertion is binary-ish.
        var i = 0;
        while (i < raw.Count && raw[i].Start <= seg.Start) i++;
        raw.Insert(i, seg);
        // DEBOUNCED, not synchronous — each rebuild now walks the full waveform (audio-level
        // segmentation), so rebuilding on every one of thousands of inserts during generation was
        // freezing the UI thread. The 50 ms debounce fires only when inserts pause.
        ScheduleTranscriptRebuild();
        _dirty = true; // cheap dirty-flag set; avoid the per-insert title churn of MarkDirty()
    }

    // Slider drags fire many events; collapse them into ~one rebuild per 50 ms.
    private void ScheduleTranscriptRebuild()
    {
        _mergeDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _mergeDebounce.Tick -= OnMergeDebounceTick;
        _mergeDebounce.Tick += OnMergeDebounceTick;
        _mergeDebounce.Stop();
        _mergeDebounce.Start();
    }

    private void OnMergeDebounceTick(object? sender, EventArgs e)
    {
        var mdT0 = Environment.TickCount64;
        try { OnMergeDebounceTickCore(); } finally { DiagnosticsLogger.NoteUiOp("OnMergeDebounceTick", mdT0, Environment.TickCount64 - mdT0); }
    }

    private void OnMergeDebounceTickCore()
    {
        _mergeDebounce?.Stop();
        NoteHeavyOp("RebuildAllTrackDisplays");
        RebuildAllTrackDisplays();
        // The same silence detection feeds auto-level chunks; recompute when any track has it on
        // (waveform arrivals and Pause/Level slider changes both land here).
        if (AnyAutoLevel) RecomputeAutoLevel();
    }

    private void RebuildAllTrackDisplays()
    {
        NoteHeavyOp("RebuildAllTrackDisplays");
        for (var t = 0; t < _trackSegments.Count; t++) RebuildTrackDisplay(t);
        // The search-match count refreshes as each track's rebuild lands (RebuildTrackDisplay is async now).
    }

    // Bubbles are detected from the ACTUAL AUDIO LEVEL (the per-track waveform peaks), not Whisper's
    // segment timing — Whisper tiles the timeline with near-zero gaps, which made the old gap-merge
    // glob a whole quiet stretch into one bubble. Here: a bubble opens when the level rises above the
    // threshold and closes once the level has stayed below it for >= _silencePauseSec. Whisper's text
    // is then dropped into whichever bubble its time overlaps. Falls back to gap-merge if the
    // waveform for this track isn't available yet.
    // Per-track generation counter: a slider drag or a burst of waveform arrivals can queue several rebuilds;
    // only the newest one's result is applied.
    private int[] _trackDisplayGen = Array.Empty<int>();

    /// <summary>
    /// Rebuild one track's transcript bubbles from its raw Whisper segments + the audio-level speech runs.
    /// The heavy part (a full scan of the track's waveform buckets - ~18M for a 150-hour project - and the
    /// segment-to-run anchoring) runs on a background task over snapshots; the result is applied on the UI
    /// thread only if nothing superseded it. Previously this ran synchronously and was the app's largest
    /// remaining UI stall (every level-slider tick and every waveform arrival paid it).
    /// </summary>
    private void RebuildTrackDisplay(int trackIdx)
    {
        if (trackIdx < 0 || trackIdx >= _trackSegments.Count) return;
        var raw = _rawSegmentsByTrack[trackIdx];
        var col = _trackSegments[trackIdx];

        if (raw.Count == 0)
        {
            col.Clear();
            _activeSegmentPerTrack[trackIdx] = null;
            TranscriptTimeline.SetTrackSegments(trackIdx, col);
            RefreshTranscriptSearchCount();
            return;
        }

        if (_trackDisplayGen.Length < _trackSegments.Count) Array.Resize(ref _trackDisplayGen, _trackSegments.Count);
        var gen = ++_trackDisplayGen[trackIdx];
        NoteHeavyOp("RebuildTrackDisplay(snapshot)"); var snapT0 = Environment.TickCount64;

        // Snapshots (UI thread). Peak arrays are never mutated in place once published, so sharing the
        // references is safe; the raw segment list IS appended to by transcription, so copy it.
        var t = trackIdx;
        var rawSnap = raw.ToList();
        var pauseSec = t < _trackPause.Length ? _trackPause[t] : DefaultPauseSec;
        var threshold = t < _trackLevel.Length ? _trackLevel[t] : DefaultLevel;
        var starts = (double[])_clipStarts.Clone();
        float[]?[]?[]? waves = null;
        if (_clipWaveforms != null)
        {
            waves = new float[_clipWaveforms.Length][][];
            for (var c = 0; c < _clipWaveforms.Length; c++)
                waves[c] = _clipWaveforms[c] is { } cell ? (float[]?[])cell.Clone() : null;
        }

        DiagnosticsLogger.NoteUiOp($"RebuildTrackDisplay.snapshot(t{t})", snapT0, Environment.TickCount64 - snapT0);
        _ = Task.Run(() =>
        {
            var result = new List<TranscriptSegment>();
            try
            {
                var runs = ComputeSpeechRuns(waves, starts, t, threshold, pauseSec)?.Select(r => (r.Start, r.End)).ToList();
                if (runs is { Count: > 0 }) BuildBubblesFromRuns(rawSnap, runs, result, pauseSec);
                else GapMergeFallback(rawSnap, result, pauseSec);
            }
            catch (Exception ex) { DiagnosticsLogger.LogException("RebuildTrackDisplay", ex); return; }

            Dispatcher.UIThread.Post(() =>
            {
                // Superseded by a newer rebuild, or the track lists were recreated (footage reload)? Drop it.
                if (t >= _trackSegments.Count || t >= _trackDisplayGen.Length || gen != _trackDisplayGen[t]) return;
                if (!ReferenceEquals(_trackSegments[t], col)) return;

                var prevActiveStart = _activeSegmentPerTrack[t]?.Start;
                col.Clear();
                col.AddRange(result);
                _activeSegmentPerTrack[t] = null;
                if (prevActiveStart is double ta)
                {
                    foreach (var sg in col)
                    {
                        if (sg.Start <= ta && ta < sg.End) { sg.IsActive = true; _activeSegmentPerTrack[t] = sg; break; }
                    }
                }
                NoteHeavyOp("RebuildTrackDisplay(apply)");
                var applyT0 = Environment.TickCount64;
                TranscriptTimeline.SetTrackSegments(t, col);
                RefreshTranscriptSearchCount();
                DiagnosticsLogger.NoteUiOp($"RebuildTrackDisplay.apply(t{t},{col.Count})", applyT0, Environment.TickCount64 - applyT0);
            });
        });
    }

    private void RefreshTranscriptSearchCount()
    {
        if (SearchCountLabel == null || string.IsNullOrEmpty(TranscriptSearchBox?.Text)) return;
        var n = TranscriptTimeline.SearchMatchCount();
        SearchCountLabel.Text = $"{n} match{(n == 1 ? "" : "es")}";
    }

    private const double WaveBucketsPerSec = 32.0;

    // Walk the track's waveform (timeline-absolute) and return [start,end] runs of speech: a run
    // opens when a bucket is >= threshold and closes once buckets have been below threshold for
    // >= _silencePauseSec. Returns null if no waveform exists for this track yet.
    private List<(double Start, double End)>? BuildSpeechRunsFromAudio(int trackIdx)
    {
        if (_clipWaveforms == null) return null;
        var threshold = trackIdx < _trackLevel.Length ? _trackLevel[trackIdx] : DefaultLevel;
        var pauseSec = trackIdx < _trackPause.Length ? _trackPause[trackIdx] : DefaultPauseSec;
        // Same detection as the auto-level map (one implementation, in ComputeSpeechRuns) — these callers
        // just don't need each run's peak.
        var runs = ComputeSpeechRuns(_clipWaveforms, _clipStarts, trackIdx, threshold, pauseSec);
        return runs?.Select(r => (r.Start, r.End)).ToList();
    }

    // Assign each Whisper segment's text to the audio run it OVERLAPS most. A segment that overlaps
    // no run keeps its own timestamp as an "orphan" bubble (grouped with nearby orphans by the pause
    // rule) — it is NEVER dumped onto a distant run, which was the bug that moved words minutes away.
    private static void BuildBubblesFromRuns(List<TranscriptSegment> raw,
        List<(double Start, double End)> runs, List<TranscriptSegment> col, double pauseSec)
    {
        var speaker = raw.Count > 0 ? raw[0].Speaker : "";
        var runTexts = new List<string>[runs.Count];
        for (var i = 0; i < runs.Count; i++) runTexts[i] = new List<string>();
        var orphans = new List<TranscriptSegment>();

        // Anchor each text segment to the audio run nearest its START (0 distance = start is inside the
        // run), capped to a tolerance. Anchoring on the start — not max overlap — stops a Whisper
        // segment with a wrong (too-late) end timestamp from being dragged onto a far-away block; if the
        // nearest run is still beyond the tolerance, the segment becomes an orphan at its own time.
        var tol = Math.Max(0.6, pauseSec);
        // Runs come out of ComputeSpeechRuns in time order and never overlap, so the nearest run to a point
        // is one of its two neighbours - found by binary search. The old linear scan was
        // O(segments x runs): 122k Whisper segments x tens of thousands of runs on a 150-hour project was a
        // ten-second UI freeze on every transcript rebuild. Falls back to the scan if runs aren't sorted.
        var sorted = true;
        for (var i = 1; i < runs.Count && sorted; i++) if (runs[i].Start < runs[i - 1].Start) sorted = false;
        foreach (var seg in raw)
        {
            if (string.IsNullOrWhiteSpace(seg.Text)) continue;
            var best = -1;
            var bestDist = double.MaxValue;
            if (sorted && runs.Count > 0)
            {
                // First run whose Start is > seg.Start (upper bound).
                int lo = 0, hi = runs.Count;
                while (lo < hi) { var mid = (lo + hi) >> 1; if (runs[mid].Start <= seg.Start) lo = mid + 1; else hi = mid; }
                if (lo - 1 >= 0)
                {
                    var r = runs[lo - 1];
                    var d = seg.Start > r.End ? seg.Start - r.End : 0.0;
                    bestDist = d; best = lo - 1;
                }
                if (lo < runs.Count)
                {
                    var d = runs[lo].Start - seg.Start;
                    if (d < bestDist) { bestDist = d; best = lo; } // strict: earlier index wins ties, as before
                }
            }
            else
            {
                for (var i = 0; i < runs.Count; i++)
                {
                    var d = seg.Start < runs[i].Start ? runs[i].Start - seg.Start
                          : seg.Start > runs[i].End ? seg.Start - runs[i].End
                          : 0.0;
                    if (d < bestDist) { bestDist = d; best = i; }
                }
            }
            if (best >= 0 && bestDist <= tol) runTexts[best].Add(seg.Text.Trim());
            else orphans.Add(seg);   // no run near its start -> keep it at its own time
        }

        var bubbles = new List<TranscriptSegment>(runs.Count + 4);
        for (var i = 0; i < runs.Count; i++)
        {
            if (runTexts[i].Count == 0) continue; // silent run (e.g. music) → no bubble
            bubbles.Add(new TranscriptSegment
            {
                Start = runs[i].Start, End = runs[i].End,
                Text = string.Join(" ", runTexts[i]), Speaker = speaker,
            });
        }

        // Orphans → their own bubbles at their own positions, split by the pause threshold.
        if (orphans.Count > 0)
        {
            orphans.Sort((a, b) => a.Start.CompareTo(b.Start));
            double cs = 0, ce = 0; var sb = new StringBuilder(); var open = false;
            void Flush() { if (open) bubbles.Add(new TranscriptSegment { Start = cs, End = ce, Text = sb.ToString().Trim(), Speaker = speaker }); }
            foreach (var s in orphans)
            {
                if (!open) { cs = s.Start; ce = s.End; sb.Clear(); sb.Append(s.Text); open = true; continue; }
                if (s.Start - ce >= pauseSec) { Flush(); cs = s.Start; ce = s.End; sb.Clear(); sb.Append(s.Text); }
                else { sb.Append(' ').Append(s.Text); ce = s.End; }
            }
            Flush();
        }

        bubbles.Sort((a, b) => a.Start.CompareTo(b.Start));
        foreach (var b in bubbles) col.Add(b);
    }

    // Fallback when no waveform is available: the old gap-between-Whisper-segments merge.
    private static void GapMergeFallback(List<TranscriptSegment> raw, List<TranscriptSegment> col, double pauseSec)
    {
        double curStart = 0, curEnd = 0;
        string curSpeaker = "";
        var sb = new StringBuilder();
        var open = false;
        foreach (var s in raw)
        {
            if (!open)
            {
                curStart = s.Start; curEnd = s.End; curSpeaker = s.Speaker;
                sb.Clear(); sb.Append(s.Text); open = true;
                continue;
            }
            var gap = Math.Max(0.0, s.Start - curEnd);
            if (gap >= pauseSec || s.Speaker != curSpeaker)
            {
                col.Add(new TranscriptSegment { Start = curStart, End = curEnd, Text = sb.ToString().Trim(), Speaker = curSpeaker });
                curStart = s.Start; curEnd = s.End; curSpeaker = s.Speaker;
                sb.Clear(); sb.Append(s.Text);
            }
            else { sb.Append(' ').Append(s.Text); curEnd = s.End; }
        }
        if (open) col.Add(new TranscriptSegment { Start = curStart, End = curEnd, Text = sb.ToString().Trim(), Speaker = curSpeaker });
    }

    // Clear the transcript (in-memory + disk cache) after a confirmation, so Generate re-transcribes
    // from scratch rather than reloading the old cached result.
    private async Task ClearTranscriptAsync()
    {
        var total = _rawSegmentsByTrack.Sum(c => c.Count);
        var message = total > 0
            ? $"Delete the current transcript ({total} lines) and its cache? You'll need to press Generate to recreate it."
            : "Delete any cached transcript for this footage so the next Generate re-transcribes from scratch?";
        if (!await ConfirmAsync("Clear transcript", message, "Clear transcript")) return;

        // Stop any in-flight transcription.
        _transcribeCts?.Cancel();
        _transcribePause.Set();
        _transcribePaused = false;
        TranscribeControls.IsVisible = false;
        PauseTranscriptBtn.Content = "⏸";
        GenerateBtn.IsEnabled = true;
        TranscriptProgress.IsVisible = false;

        // Empty the in-memory transcript + each column.
        for (var t = 0; t < _rawSegmentsByTrack.Count; t++)
        {
            _rawSegmentsByTrack[t].Clear();
            if (t < _trackSegments.Count)
            {
                _trackSegments[t].Clear();
                TranscriptTimeline.SetTrackSegments(t, _trackSegments[t]);
            }
        }
        for (var i = 0; i < _activeSegmentPerTrack.Length; i++) _activeSegmentPerTrack[i] = null;

        DeleteTranscriptCacheForCurrentSources();

        TranscriptStatus.Text = "Transcript cleared. Press Generate to recreate it.";
        MarkDirty();
    }

    private void DeleteTranscriptCacheForCurrentSources()
    {
        if (_currentSources == null) return;
        var cacheDir = Path.Combine(CacheRoot, "transcript");
        if (!Directory.Exists(cacheDir)) return;
        foreach (var src in _currentSources)
        {
            try
            {
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) continue;
                var fi = new FileInfo(src);
                // Matches every track/model variant cached for this exact source file.
                var prefix = $"{SafeStem(src)}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks:X}_a";
                foreach (var f in Directory.GetFiles(cacheDir, prefix + "*"))
                {
                    try { File.Delete(f); } catch { /* best effort */ }
                }
            }
            catch { /* skip this source */ }
        }
    }

    // Generic destructive-action confirm dialog (returns true if the user clicks the confirm button).
    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new Window
        {
            Title = title, Width = 430, Height = 175,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false, Background = Brush.Parse("#0F0F14"),
        };
        var ok = new Button { Content = confirmText, MinWidth = 110, Background = Brush.Parse("#7A2E33"), Foreground = Brushes.White, Focusable = false };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Focusable = false };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };
        var body = new TextBlock
        {
            Text = message, Foreground = Brush.Parse("#E2E2EA"),
            Margin = new Avalonia.Thickness(16, 18, 16, 0), TextWrapping = TextWrapping.Wrap,
        };
        var layout = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(body, Avalonia.Controls.Dock.Top);
        layout.Children.Add(body);
        layout.Children.Add(new Border { Child = buttons, Margin = new Avalonia.Thickness(16, 12, 16, 0) });
        box.Content = layout;

        var result = false;
        ok.Click += (_, _) => { result = true; box.Close(); };
        cancel.Click += (_, _) => { result = false; box.Close(); };
        await box.ShowDialog(this);
        return result;
    }

    // Transcribes EVERY audio track of each source on the GPU (per-source, then shifted onto the
    // timeline). Each track gets its own column in the UI; if a track is silent the column stays
    // empty. Results cache to disk, so re-running or reopening a project is instant.
    // How many source clips still need transcription (a clip is "done" only when every track is cached).
    private int CountClipsNeedingTranscription(EngineConfig cfg, List<int> tracks)
    {
        if (_currentSources == null) return 0;
        var cacheDir = Path.Combine(CacheRoot, "transcript");

        // Discover which track positions are ACTUALLY cached per clip (glob the stable key prefix) rather
        // than asserting indices 0..liveTrackCount-1. mpv occasionally reports an extra/phantom audio
        // track that was never transcribed; the old tracks.All(File.Exists) then failed on that index for
        // EVERY clip and reported "0 done". The union of cached positions across clips is the real set.
        var perClip = new List<HashSet<int>>();
        var union = new HashSet<int>();
        foreach (var src in _currentSources)
        {
            var cached = new HashSet<int>();
            try
            {
                if (!string.IsNullOrEmpty(src) && File.Exists(src) && Directory.Exists(cacheDir))
                {
                    var fi = new FileInfo(src);
                    var prefix = $"{SafeStem(src)}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks:X}_a";
                    var suffix = $"_{cfg.Model}.json";
                    foreach (var f in Directory.GetFiles(cacheDir, prefix + "*" + suffix))
                    {
                        var name = Path.GetFileName(f);
                        var mid = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
                        if (int.TryParse(mid, out var t)) { cached.Add(t); union.Add(t); }
                    }
                }
            }
            catch { /* unreadable → treated as not cached */ }
            perClip.Add(cached);
        }

        // "Needed" = the tracks present in the cache anywhere (ignores phantom live tracks never
        // transcribed). If nothing is cached at all, fall back to the requested track set.
        var needed = union.Count > 0 ? union : new HashSet<int>(tracks);
        var remaining = 0;
        foreach (var cached in perClip)
            if (!needed.All(cached.Contains)) remaining++;
        return remaining;
    }

    /// <summary>
    /// Kick off transcription once footage is actually loaded. Clips and the audio-track list both
    /// arrive asynchronously after a project opens, so wait for them rather than firing into an empty
    /// timeline. Gives up quietly if nothing loads.
    /// </summary>
    private async Task AutoGenerateTranscriptWhenReadyAsync()
    {
        for (var i = 0; i < 100; i++) // ~20 s
        {
            await System.Threading.Tasks.Task.Delay(200);
            if (_currentSources is { Count: > 0 } && _audioConfigured)
            {
                DiagnosticsLogger.Log("transcribe: auto-generate on load");
                await GenerateTranscriptAsync(skipConfirm: true);
                return;
            }
        }
    }

    private async Task GenerateTranscriptAsync(bool skipConfirm = false)
    {
        if (_currentSources == null || _currentSources.Count == 0)
        {
            TranscriptStatus.Text = "Open footage first.";
            return;
        }

        EngineConfig? cfg = null;
        var cfgPath = WhisperConfigFile();
        if (cfgPath != null)
        {
            try { cfg = JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(cfgPath), EngineJsonOpts); }
            catch { cfg = null; }
        }
        var scriptPath = WhisperFile(EngineScriptName(cfg));
        DiagnosticsLogger.Log($"transcribe: cfg={cfgPath ?? "<none>"} script={scriptPath ?? "<none>"} " +
            $"python={cfg?.Python ?? "<none>"} pythonExists={(cfg != null && !string.IsNullOrEmpty(cfg.Python) && File.Exists(cfg.Python))} " +
            $"model={cfg?.Model ?? "<none>"}");
        if (cfg == null || scriptPath == null || string.IsNullOrEmpty(cfg.Python) || !File.Exists(cfg.Python))
        {
            TranscriptStatus.Text = "Transcription engine isn't installed yet — pick a model and I'll set it up.";
            return;
        }

        var tracks = new List<int>();
        for (var p = 0; p < Math.Max(_audioIds.Length, _audioTitles.Length); p++) tracks.Add(p);
        if (tracks.Count == 0) tracks.Add(0);

        // Confirm, showing how many clips still need transcribing (the rest are already cached).
        var totalClips = _currentSources.Count;
        var remaining = CountClipsNeedingTranscription(cfg, tracks);
        var msg = remaining == 0
            ? $"All {totalClips} clips are already transcribed. Re-transcribe anyway?"
            : $"Transcribe {remaining} of {totalClips} clips? ({totalClips - remaining} already done.)";
        if (!skipConfirm && !await ConfirmAsync("Generate transcript", msg, "Generate")) return;

        _transcribeCts?.Cancel();
        _transcribeCts = new CancellationTokenSource();
        var ct = _transcribeCts.Token;

        var sources = _currentSources.ToList();
        var starts = _clipStarts.ToArray();
        var durations = _clipDurations.ToArray();
        var titles = _audioTitles.ToArray();

        _transcribeTotal = sources.Count * tracks.Count;
        _transcribeDone = 0;
        UpdateTranscriptProgress();
        TranscriptStatus.Text = $"Transcribing {tracks.Count} track(s) on the GPU…";
        GenerateBtn.IsEnabled = false;
        GenerateBtn.Content = "Generating…";
        TranscribeControls.IsVisible = true; // pause + stop icons light up while running
        PauseTranscriptBtn.Content = "⏸";
        _transcribePause.Set();
        _transcribePaused = false;

        try
        {
            await Task.Run(() => TranscribeWorker(sources, starts, durations, titles, tracks, cfg, scriptPath, ct), ct);
            if (!ct.IsCancellationRequested)
            {
                TranscriptProgress.IsVisible = false;
                var total = _rawSegmentsByTrack.Sum(c => c.Count);
                TranscriptStatus.Text = total > 0
                    ? $"Transcript ready · {total} lines."
                    : "No speech found (or the engine errored — check the model is installed).";
            }
        }
        catch (OperationCanceledException) { /* superseded by a new load */ }
        finally
        {
            GenerateBtn.IsEnabled = true;
            GenerateBtn.Content = "Generate";
            TranscribeControls.IsVisible = false;
            PauseTranscriptBtn.Content = "⏸";
            _transcribePause.Set();
            // Final coalesced rebuild + refresh the dirty/save indicator (per-insert used the cheap
            // _dirty flag without touching the title).
            RebuildAllTrackDisplays();
            MarkDirty();
        }
    }

    private void TranscribeWorker(List<string> sources, double[] starts, double[] durations, string[] titles,
        List<int> tracks, EngineConfig cfg, string script, CancellationToken ct)
    {
        var cacheDir = Path.Combine(CacheRoot, "transcript");
        Directory.CreateDirectory(cacheDir);

        var pending = new List<(int clip, int pos)>();
        for (var c = 0; c < sources.Count; c++)
            foreach (var p in tracks) pending.Add((c, p));

        // Progress should reflect only the work that ACTUALLY needs doing — pairs already cached on disk (this
        // session or a previous one before a restart) are applied but cost ~0, so they shouldn't inflate x/N.
        // Count the cache MISSES up front so a restart mid-transcription shows the real remaining count.
        var needCount = 0;
        foreach (var (c, p) in pending)
        {
            var s = sources[c];
            if (string.IsNullOrEmpty(s) || !File.Exists(s)) { needCount++; continue; }
            try
            {
                var f = new FileInfo(s);
                var ck = Path.Combine(cacheDir, $"{SafeStem(s)}_{f.Length}_{f.LastWriteTimeUtc.Ticks:X}_a{p}_{cfg.Model}.json");
                if (!File.Exists(ck)) needCount++;
            }
            catch { needCount++; }
        }
        Dispatcher.UIThread.Post(() => { _transcribeTotal = needCount; _transcribeDone = 0; UpdateTranscriptProgress(); });

        // Same playhead-priority as the waveform worker: the clip under the cursor and clips to its
        // right go first, re-evaluated before EACH item so seeking mid-run re-prioritizes.
        while (pending.Count > 0 && !ct.IsCancellationRequested)
        {
            // Honor pause — block here when the user pressed Pause.
            _transcribePause.Wait(ct);
            if (ct.IsCancellationRequested) return;

            var ph = _lastPlayhead;
            var bestIdx = 0;
            var bestKey = double.MaxValue;
            for (var i = 0; i < pending.Count; i++)
            {
                var (c, tr) = pending[i];
                var start = c < starts.Length ? starts[c] : 0;
                var dur = c < durations.Length ? durations[c] : 0;
                var pk = (dur > 0 && start + dur <= ph) ? 1e12 + (ph - start) : Math.Max(0, start - ph);
                pk = pk * 16 + tr;
                if (pk < bestKey) { bestKey = pk; bestIdx = i; }
            }
            var (clip, pos) = pending[bestIdx];
            pending.RemoveAt(bestIdx);

            if (ct.IsCancellationRequested) return;
            var src = sources[clip];
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) { BumpTranscribe(); continue; }

            FileInfo fi;
            try { fi = new FileInfo(src); } catch { BumpTranscribe(); continue; }

            var key = $"{SafeStem(src)}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks:X}_a{pos}_{cfg.Model}.json";
            var cachePath = Path.Combine(cacheDir, key);

            EngineOut? eo = null;
            var fromCache = false;
            var cacheExisted = File.Exists(cachePath);
            if (cacheExisted)
            {
                try { eo = JsonSerializer.Deserialize<EngineOut>(File.ReadAllText(cachePath), EngineJsonOpts); fromCache = eo != null; }
                catch { eo = null; }
            }
            if (eo == null)
            {
                // A cache file that existed but was corrupt wasn't counted as "needed" up front — count it now
                // so this re-transcription doesn't push _transcribeDone past _transcribeTotal (which would hide
                // the progress bar early).
                if (cacheExisted) System.Threading.Interlocked.Increment(ref _transcribeTotal);
                eo = RunEngineForTrack(src, pos, cfg, script, ct);
                if (eo == null) { BumpTranscribe(); continue; }
                try { File.WriteAllText(cachePath, JsonSerializer.Serialize(eo, EngineJsonOpts)); } catch { }
            }

            if (ct.IsCancellationRequested) return;
            var clipStart = clip < starts.Length ? starts[clip] : 0;
            var speaker = pos < titles.Length ? titles[pos] : $"Track {pos + 1}";
            var trackIdx = pos;
            var segs = eo.Segments;
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var s in segs)
                {
                    if (string.IsNullOrWhiteSpace(s.Text)) continue;
                    InsertSegmentSorted(trackIdx, new TranscriptSegment
                    {
                        Start = s.Start + clipStart, End = s.End + clipStart,
                        Text = s.Text.Trim(), Speaker = speaker,
                    });
                }
            });
            if (!fromCache) BumpTranscribe(); // cache hits were applied but aren't part of the remaining-work count
        }
    }

    private EngineOut? RunEngineForTrack(string src, int pos, EngineConfig cfg, string script, CancellationToken ct)
    {
        var ffmpeg = ToolPath("ffmpeg.exe");
        if (ffmpeg == null) return null;
        var tmpWav = Path.Combine(Path.GetTempPath(), $"fr_tx_{Guid.NewGuid():N}.wav");
        var tmpJson = Path.Combine(Path.GetTempPath(), $"fr_tx_{Guid.NewGuid():N}.json");
        try
        {
            var ff = new ProcessStartInfo
            {
                FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-y", "-v", "error", "-i", src, "-map", $"0:a:{pos}",
                                      "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", tmpWav })
                ff.ArgumentList.Add(a);
            using (var p = ChildProcesses.Start(ff))
            {
                p.StandardError.ReadToEnd();
                WaitForExitOrCancel(p, ct, int.MaxValue);
                if (ct.IsCancellationRequested) return null;
                if (p.ExitCode != 0 || !File.Exists(tmpWav)) return null;
            }
            if (ct.IsCancellationRequested) return null;

            var pe = new ProcessStartInfo
            {
                FileName = cfg.Python, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory,
            };
            foreach (var a in new[] { script, "--audio", tmpWav, "--out", tmpJson,
                                      "--model", cfg.Model, "--device", cfg.Device, "--compute", cfg.Compute })
                pe.ArgumentList.Add(a);
            ShareToolPath(pe);
            using (var p = ChildProcesses.Start(pe))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                _ = p.StandardOutput.ReadToEndAsync();
                if (!WaitForExitOrCancel(p, ct, int.MaxValue)) return null;
                if (p.ExitCode != 0)
                {
                    // The sidecar's stderr is the only place that says WHY (missing package, bad model
                    // name, unreadable audio). Dropping it turns every failure into a mute
                    // "no speech found" in the UI.
                    var err = "";
                    try { err = errTask.GetAwaiter().GetResult(); } catch { /* nothing to add */ }
                    if (err.Length > 800) err = err[^800..];
                    DiagnosticsLogger.Log($"transcribe sidecar exit {p.ExitCode}: python={cfg.Python} " +
                        $"script={script} model={cfg.Model}\n{err.Trim()}");
                    return null;
                }
            }
            if (!File.Exists(tmpJson)) { DiagnosticsLogger.Log($"transcribe sidecar wrote no output: {tmpJson}"); return null; }
            return JsonSerializer.Deserialize<EngineOut>(File.ReadAllText(tmpJson), EngineJsonOpts);
        }
        catch (Exception ex) { DiagnosticsLogger.LogException("transcribe sidecar", ex); return null; }
        finally
        {
            try { File.Delete(tmpWav); } catch { }
            try { File.Delete(tmpJson); } catch { }
        }
    }

    private void BumpTranscribe()
    {
        Interlocked.Increment(ref _transcribeDone);
        Dispatcher.UIThread.Post(UpdateTranscriptProgress);
    }

    private void UpdateTranscriptProgress()
    {
        if (_transcribeTotal <= 0 || _transcribeDone >= _transcribeTotal)
        {
            TranscriptProgress.IsVisible = false;
            return;
        }
        TranscriptProgress.Maximum = _transcribeTotal;
        TranscriptProgress.Value = _transcribeDone;
        TranscriptProgress.IsVisible = true;
        TranscriptStatus.Text = $"Transcribing… {_transcribeDone}/{_transcribeTotal} track-clips";
    }

    // mpv EDL: each segment is length-prefixed (%bytes%path) so Windows paths survive. Concatenated
    // segments play as one seekable virtual timeline with no documented duration cap.
    private static string BuildEdl(IEnumerable<string> sources)
    {
        var sb = new StringBuilder("edl://");
        foreach (var s in sources)
        {
            var len = Encoding.UTF8.GetByteCount(s);
            sb.Append('%').Append(len).Append('%').Append(s).Append(';');
        }
        return sb.ToString();
    }

    // ---- Helpers -----------------------------------------------------------

    private static double? TryGet(Func<double?> get)
    {
        try { return get(); }
        catch { return null; }
    }

    // Read mpv properties through the STRING api only. The binding's typed getters (Duration.Get(),
    // PlaybackTime.Get(), Pause.Get()) post an async request and wait for a reply on the mpv event
    // loop; on macOS that reply never arrives, so the first call wedges the UI thread forever and
    // Tick() dies before it can configure audio. GetStr routes to libmpv directly there.
    private double? MpvNum(string prop)
        => double.TryParse(GetStr(prop), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
           ? v : null;

    private bool? MpvFlag(string prop)
    {
        var s = GetStr(prop);
        if (string.IsNullOrEmpty(s)) return null;
        return s is "yes" or "true" or "1";
    }

    private string? GetStr(string prop)
    {
        try
        {
            // macOS: go straight to libmpv — the binding's async read path never returns here.
            if (OperatingSystem.IsMacOS() && _mpv != null && Util.MacNative.CanReadProperties)
                return Util.MacNative.GetPropertyString(MpvHandle(_mpv), prop);
            return _mpv!.GetPropertyString(prop);
        }
        catch { return null; }
    }

    // MpvContextBase.Ctx is protected, so reach the mpv_handle* through its backing field. Cached —
    // this runs on every property read.
    private static System.Reflection.FieldInfo? _ctxField;
    private static bool _ctxFieldSearched;

    /// <summary>The raw mpv_handle* behind a context, as an IntPtr (Zero if it can't be reached).</summary>
    private static unsafe IntPtr MpvHandle(HanumanInstitute.LibMpv.MpvContext ctx)
    {
        if (!_ctxFieldSearched)
        {
            _ctxFieldSearched = true;
            for (var t = ctx.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField("_ctx", System.Reflection.BindingFlags.NonPublic
                                         | System.Reflection.BindingFlags.Instance);
                if (f != null) { _ctxField = f; break; }
            }
        }
        var boxed = _ctxField?.GetValue(ctx);
        if (boxed == null) return IntPtr.Zero;
        return (IntPtr)System.Reflection.Pointer.Unbox(boxed);
    }

    private int? TryGetInt(string prop)
        => int.TryParse(GetStr(prop), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Inv(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// The sidecar script named by the engine config. The field has always existed but was ignored —
    /// the launcher hardcoded transcribe.py, so pointing an engine at a different script silently ran
    /// the wrong one. Falls back to the historical name when unset.
    /// </summary>
    private static string EngineScriptName(EngineConfig? cfg)
        => string.IsNullOrWhiteSpace(cfg?.Script) ? "transcribe.py" : cfg!.Script!;

    /// <summary>
    /// Put the ffmpeg we resolved onto a child process's PATH. Sidecars that shell out to ffmpeg
    /// otherwise fail with "FFmpeg is not installed or not in your PATH" even though the app itself
    /// located it fine — a GUI process does not necessarily hand its children a useful PATH.
    /// </summary>
    private static void ShareToolPath(ProcessStartInfo psi)
    {
        try
        {
            var dirs = new List<string>();
            foreach (var tool in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var resolved = ToolPath(tool);
                var dir = resolved == null ? null : Path.GetDirectoryName(resolved);
                if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir)) dirs.Add(dir);
            }
            if (dirs.Count == 0) return;

            var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = string.Join(Path.PathSeparator, dirs) +
                                      (existing.Length > 0 ? Path.PathSeparator + existing : "");
        }
        catch { /* best effort — the sidecar may still find ffmpeg on its own */ }
    }

    /// <summary>
    /// Locate a bundled command-line tool. Callers pass the Windows name ("ffmpeg.exe"); on Unix the
    /// ".exe" is stripped. We look beside the exe, then in native/&lt;rid&gt;/ for a dev-tree run, then fall
    /// back to whatever is on PATH (Homebrew's /opt/homebrew/bin on macOS, /usr/bin on Linux).
    /// </summary>
    private static string? ToolPath(string exe)
    {
        var name = OperatingSystem.IsWindows()
            ? exe
            : (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe);

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, name),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "native", NativeRid, name)),
        };

        // PATH lookup — how ffmpeg/ffprobe are normally installed on macOS and Linux.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir, name)); } catch { /* malformed PATH entry */ }
        }
        if (!OperatingSystem.IsWindows())
        {
            // GUI apps launched from Finder inherit a minimal PATH that omits Homebrew.
            candidates.Add("/opt/homebrew/bin/" + name);
            candidates.Add("/usr/local/bin/" + name);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Runtime identifier used for the native/&lt;rid&gt;/ tool folder.</summary>
    private static string NativeRid =>
        OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS()
            ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64")
        : "linux-x64";

    private static double ProbeDuration(string probe, string file)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = probe, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file })
                psi.ArgumentList.Add(a);
            using var p = ChildProcesses.Start(psi);
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return double.TryParse(outp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch { return 0; }
    }

    private static string Fmt(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var ts = TimeSpan.FromSeconds(s);
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    // ---- Settings + auto-save + close-confirm ------------------------------

    private async Task OpenSettingsDialog()
    {
        var ffmpeg = ToolPath("ffmpeg.exe");
        // Enumerate mics + read the Windows default capture device off the UI thread (both can take a moment).
        var mics = ffmpeg != null ? await Task.Run(() => DictationService.EnumerateMicrophones(ffmpeg)) : null;
        var defaultMic = await Task.Run(() => WindowsAudio.GetDefaultCaptureName());
        var dlg = new SettingsWindow(_settings, mics, defaultMic);
        // Live previews — apply as the user drags, revert if they Cancel.
        var origSkip = _settings.SkipSeconds;
        var origFont = _settings.TranscriptFontSize;
        dlg.SkipSecondsPreview += s => { _settings.SkipSeconds = s; UpdateSkipPreview(); };
        dlg.FontSizePreview += f => TranscriptTimeline.SetFontSize(f);

        var result = await dlg.ShowDialog<AppSettings?>(this);
        if (result != null)
        {
            _settings = result;
            ConfigureAutoSaveTimer();
            _autoLevelTarget = _settings.AutoLevelMax;
            if (AnyAutoLevel) RecomputeAutoLevel();
            UpdateSkipPreview();
            TranscriptTimeline.SetFontSize(_settings.TranscriptFontSize);
            RefreshCopyOptionChecks(); // keep the in-tab ⋮ menu in sync
            ApplySpellSettings();      // (re)load or clear the spell-checker per the new toggles
            // A "Regenerate …" button was clicked — run it now that the dialog has closed (progress shows
            // on the toolbar). Settings were already applied above.
            if (dlg.RequestedRegen == "thumbs") await RegenerateAssetAsync(thumbnails: true);
            else if (dlg.RequestedRegen == "waves") await RegenerateAssetAsync(thumbnails: false);
        }
        else
        {
            // Cancelled → undo any live previews.
            _settings.SkipSeconds = origSkip;
            _settings.TranscriptFontSize = origFont;
            UpdateSkipPreview();
            TranscriptTimeline.SetFontSize(origFont);
        }
    }

    // Push the saved copy-options back onto the in-tab ⋮ menu checkboxes (two-way sync via _settings).
    private void RefreshCopyOptionChecks()
    {
        CopyOnlyNewChk.IsChecked = _settings.CopyOnlyUncopied;
        CopyHeadersChk.IsChecked = _settings.CopyFolderHeaders;
        CopyTimestampsChk.IsChecked = _settings.CopyTimestamps;
        CopyDayHeadingsChk.IsChecked = _settings.CopyDayHeadings;
    }

    private void ConfigureAutoSaveTimer()
    {
        if (_autoSaveTimer == null)
        {
            _autoSaveTimer = new DispatcherTimer();
            _autoSaveTimer.Tick += async (_, _) => await AutoSaveTickAsync();
        }
        _autoSaveTimer.Stop();
        if (_settings.AutoSaveEnabled && _settings.AutoSaveIntervalSeconds >= 5)
        {
            _autoSaveTimer.Interval = TimeSpan.FromSeconds(_settings.AutoSaveIntervalSeconds);
            _autoSaveTimer.Start();
        }
    }

    // The minute timer: keep the main file current AND write a rotating versioned backup.
    private async Task AutoSaveTickAsync()
    {
        await SaveMainFileSilentAsync();
        await RunMinuteBackupAsync();
    }

    // Save the main project file silently (no big toast). Used by the minute timer and after each log.
    private async Task SaveMainFileSilentAsync()
    {
        if (!_dirty) return;
        if (string.IsNullOrEmpty(_currentProjectPath)) return; // never silently prompt
        // Note: a logs-only project (path set, no footage yet — e.g. logs imported before any clips) IS valid
        // and must persist, so don't bail on an empty source list here.
        await SaveProjectAsync(forceDialog: false, silent: true);
    }

    // Versioned rotating backup: each minute (if content changed) drop a lightweight dated snapshot in
    // a "<project>.backups" folder; every 10th snapshot is promoted to a permanent checkpoint and the
    // 9 minute-snapshots before it are deleted — leaving a trail of ~10-minute checkpoints. Skips
    // entirely when nothing changed since the last snapshot.
    private long _lastBackupSeq = -1;
    private async Task RunMinuteBackupAsync()
    {
        if (string.IsNullOrEmpty(_currentProjectPath)) return;
        if (_changeSeq == _lastBackupSeq) return; // nothing changed → no new save (logs-only projects included)
        _lastBackupSeq = _changeSeq;
        try
        {
            var dir = Path.GetDirectoryName(_currentProjectPath)!;
            var stem = Path.GetFileNameWithoutExtension(_currentProjectPath);
            var backupDir = Path.Combine(dir, stem + ".backups");
            Directory.CreateDirectory(backupDir);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var minPath = Path.Combine(backupDir, $"{stem}_min_{ts}.frproj");
            await WriteProjectFileAsync(minPath, BuildProjectDto(includeAssets: false)); // lightweight

            // Promote every 10th minute-snapshot to a permanent checkpoint, deleting the other 9.
            var minFiles = Directory.GetFiles(backupDir, $"{stem}_min_*.frproj").OrderBy(f => f).ToList();
            if (minFiles.Count >= 10)
            {
                var newest = minFiles[^1];
                var keepPath = Path.Combine(backupDir, $"{stem}_keep_{ts}.frproj");
                File.Move(newest, keepPath, overwrite: true);
                foreach (var f in minFiles)
                    if (!string.Equals(f, newest, StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(f); } catch { /* best effort */ }
            }
        }
        catch (Exception ex) { ClipInfo.Text = "backup failed: " + ex.Message; }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) { TearEverythingDown(); return; }
        if (!_settings.ConfirmOnClose || !_dirty) { TearEverythingDown(); return; }

        e.Cancel = true; // intercept; we re-issue Close after the user decides

        var box = new Window
        {
            Title = "Unsaved changes",
            Width = 380, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false, Background = Brush.Parse("#0F0F14"),
        };
        var save = new Button { Content = "Save", MinWidth = 80, Background = Brush.Parse("#274050"), Foreground = Brushes.White, Focusable = false };
        var discard = new Button { Content = "Discard", MinWidth = 80, Focusable = false };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, Focusable = false };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { save, discard, cancel },
        };
        var body = new TextBlock
        {
            Text = "You have unsaved changes. Save before closing?",
            Foreground = Brush.Parse("#E2E2EA"), Margin = new Avalonia.Thickness(16, 18, 16, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var layout = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(body, Avalonia.Controls.Dock.Top);
        layout.Children.Add(body);
        layout.Children.Add(new Border { Child = buttons, Margin = new Avalonia.Thickness(16, 12, 16, 0) });
        box.Content = layout;

        string? choice = null;
        save.Click += (_, _) => { choice = "save"; box.Close(); };
        discard.Click += (_, _) => { choice = "discard"; box.Close(); };
        cancel.Click += (_, _) => { choice = "cancel"; box.Close(); };

        await box.ShowDialog(this);

        if (choice == "cancel" || choice == null) { _goingHome = false; return; } // stay open
        if (choice == "save")
        {
            await SaveProjectAsync(forceDialog: false);
            if (_dirty) return; // save failed/canceled; keep window open
        }
        _closeConfirmed = true;
        Close();
    }

    private bool _tornDown;

    // One-shot, idempotent teardown. Cancels background work, kills every ffmpeg/ffprobe/python we
    // started, stops timers, frees mpv. The Job Object set up in Program.cs is the OS-level backstop
    // for cases where this never runs (hard kill, Task Manager).
    private void TearEverythingDown()
    {
        if (_tornDown) return;
        _tornDown = true;

        try { _extractCts?.Cancel(); } catch { }
        try { _transcribeCts?.Cancel(); } catch { }
        try { _transcribePause?.Set(); } catch { } // unblock any worker waiting on pause
        try { _timer?.Stop(); } catch { }
        try { _smoothTimer?.Stop(); } catch { }
        try { _autoSaveTimer?.Stop(); } catch { }
        try { _audioRebuildTimer?.Stop(); } catch { }
        try { _dictationDismissTimer?.Stop(); } catch { }
        try { GlobalMediaKey.Uninstall(); } catch { }
        try { _settings.Save(); } catch { }

        ChildProcesses.KillAll();
    }
}

